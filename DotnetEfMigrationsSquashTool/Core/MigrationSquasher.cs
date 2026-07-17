using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text;
using System.Text.RegularExpressions;

public class MigrationSquasher
{
    private const string EfCoreVersion = "10.0.3";
    private readonly string _projectPath;
    private readonly string _contextName;
    private readonly string _migrationsFolder;
    private readonly string _projectDirectory;

    public MigrationSquasher(string projectPath, string contextName, string migration)
    {
        _projectPath = projectPath;
        _contextName = contextName;
        _projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        _migrationsFolder = Path.Combine(migration, "Migrations");
    }

    public async Task PreviewSquashAsync(string newMigrationName)
    {
        var migrations = GetExistingMigrations();

        Console.WriteLine($"📋 Found {migrations.Count} existing migrations:");
        foreach (var migration in migrations)
        {
            Console.WriteLine($"  • {migration}");
        }

        Console.WriteLine($"\n🎯 Would create new migration: {newMigrationName}");
        Console.WriteLine($"📁 Migrations folder: {_migrationsFolder}");

        var migrationFiles = GetMigrationFiles();
        Console.WriteLine($"\n📄 Files that would be removed ({migrationFiles.Count}):");
        foreach (var file in migrationFiles)
        {
            Console.WriteLine($"  • {Path.GetFileName(file)}");
        }

        Console.WriteLine("\n💡 To actually perform the squash, run without --dry-run");
        Console.WriteLine("⚠️  Make sure to backup your project first!");
    }

    public async Task SquashMigrationsAsync(string migrationName)
    {
        try
        {
            Console.WriteLine("🚀 Starting migration squash process...");
            Console.WriteLine();

            // Step 1: Validate migrations exist
            var existingMigrations = GetExistingMigrations();
            if (existingMigrations.Count == 0)
            {
                Console.WriteLine("❌ No migrations found to squash!");
                return;
            }

            Console.WriteLine($"📋 Found {existingMigrations.Count} migrations to squash");

            // Step 2: Create backup
            await CreateBackupAsync();

            // Step 3: Get current model snapshot
            var modelSnapshot = GetModelSnapshot();

            // Step 4: Extract schema BEFORE removing files
            var extractedSchema = await ExtractSchemaFromExistingMigrationsAsync();

            // Step 5: Extract down methods BEFORE removing files
            Console.WriteLine("🔍 Extracting Down methods from existing migrations...");
            var extractedDown = ExtractDownMethodsAsync();

            // Step 6: Remove old migration files
            RemoveOldMigrations();

            // Step 7: Create new consolidated migration with both Up and Down
            var migrationId = await CreateConsolidatedMigrationAsync(migrationName, extractedSchema, extractedDown);

            // Step 8: Generate database update script
            GenerateDatabaseUpdateScript(migrationName, migrationId);

            Console.WriteLine();
            Console.WriteLine("✅ Migration squash completed successfully!");
            Console.WriteLine();
            Console.WriteLine("📋 Next steps:");
            Console.WriteLine("1. Review the generated consolidated migration");
            Console.WriteLine("2. Update existing databases using the generated SQL script");
            Console.WriteLine("3. Test thoroughly before deploying to production");
            Console.WriteLine();
            Console.WriteLine($"📄 SQL script generated: {Path.Combine(_migrationsFolder, "UpdateExistingDatabases.sql")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error during migration squash: {ex.Message}");
            Console.WriteLine("🔄 Restore from backup if needed");
            throw;
        }
    }

    private List<string> GetExistingMigrations()
    {
        if (!Directory.Exists(_migrationsFolder))
            return new List<string>();

        return Directory.GetFiles(_migrationsFolder, "*.cs")
            .Where(f => !f.EndsWith("ModelSnapshot.cs"))
            .Where(f => !f.EndsWith(".Designer.cs"))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name != null && name.Length > 15 && name.Substring(0, 14).All(char.IsDigit))
            .OrderBy(x => x)
            .ToList()!;
    }

    private List<string> GetMigrationFiles()
    {
        if (!Directory.Exists(_migrationsFolder))
            return new List<string>();

        return Directory.GetFiles(_migrationsFolder, "*.cs")
            .Where(f => !f.EndsWith("ModelSnapshot.cs"))
            .ToList();
    }

    private async Task CreateBackupAsync()
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupFolder = Path.Combine(_projectDirectory, $"MigrationsBackup_{timestamp}");

        Console.WriteLine($"💾 Creating backup in: {backupFolder}");
        Directory.CreateDirectory(backupFolder);

        foreach (var file in Directory.GetFiles(_migrationsFolder, "*.cs"))
        {
            var fileName = Path.GetFileName(file);
            // Keep backups inside the project but outside the SDK's default **/*.cs compile glob.
            var backupPath = Path.Combine(backupFolder, $"{fileName}.bak");
            File.Copy(file, backupPath);
        }

        Console.WriteLine($"✅ Backup created with {Directory.GetFiles(backupFolder).Length} files");
        await Task.CompletedTask;
    }

    private string GetModelSnapshot()
    {
        var snapshotFile = Directory.GetFiles(_migrationsFolder, "*ModelSnapshot.cs").FirstOrDefault();
        if (snapshotFile != null && File.Exists(snapshotFile))
        {
            Console.WriteLine("📸 Found model snapshot");
            return File.ReadAllText(snapshotFile);
        }

        Console.WriteLine("⚠️  No model snapshot found");
        return string.Empty;
    }

    private async Task<string> ExtractSchemaFromExistingMigrationsAsync()
    {
        Console.WriteLine("🔍 Extracting schema from existing migrations...");

        var migrationFiles = GetMigrationFiles()
            .Where(f => !f.EndsWith(".Designer.cs"))
            .OrderBy(f => f)
            .ToList();

        var consolidatedUp = new StringBuilder();

        foreach (var file in migrationFiles)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file);
                var upMethodContent = ExtractUpMethodContent(content);

                if (!string.IsNullOrWhiteSpace(upMethodContent))
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    Console.WriteLine($"  📄 Processing: {fileName}");

                    consolidatedUp.AppendLine($"            // From {fileName}");
                    consolidatedUp.AppendLine(upMethodContent);
                    consolidatedUp.AppendLine();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Could not parse {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (consolidatedUp.Length > 0)
        {
            Console.WriteLine($"✅ Extracted schema from {migrationFiles.Count} migrations");
            return consolidatedUp.ToString().TrimEnd();
        }

        Console.WriteLine("⚠️  No schema commands found in existing migrations");
        return GenerateTodoSchema();
    }

    private string ExtractUpMethodContent(string migrationContent)
    {
        try
        {
            // Find the Up method
            var upMethodPattern = @"protected\s+override\s+void\s+Up\s*\(\s*MigrationBuilder\s+migrationBuilder\s*\)\s*{";
            var match = Regex.Match(migrationContent, upMethodPattern);

            if (!match.Success) return string.Empty;

            var braceStart = match.Index + match.Length - 1;
            var braceCount = 1;
            var pos = braceStart + 1;

            while (pos < migrationContent.Length && braceCount > 0)
            {
                if (migrationContent[pos] == '{') braceCount++;
                else if (migrationContent[pos] == '}') braceCount--;
                pos++;
            }

            if (braceCount == 0)
            {
                var methodContent = migrationContent.Substring(braceStart + 1, pos - braceStart - 2);
                return methodContent.Trim();
            }
        }
        catch (Exception)
        {
            // Fallback to simpler method
        }

        return string.Empty;
    }



    private void RemoveOldMigrations()
    {
        Console.WriteLine("🗑️  Removing old migration files...");

        var filesToRemove = GetMigrationFiles();
        foreach (var file in filesToRemove)
        {
            Console.WriteLine($"   Removing: {Path.GetFileName(file)}");
            File.Delete(file);
        }

        Console.WriteLine($"✅ Removed {filesToRemove.Count} migration files");
    }

    private async Task<string> CreateConsolidatedMigrationAsync(string migrationName, string extractedSchema, string extractedDown)
    {
        Console.WriteLine($"📝 Creating consolidated migration: {migrationName}");

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var className = $"{timestamp}_{migrationName}";

        var migrationContent = GenerateConsolidatedMigrationContent(className, migrationName, extractedSchema, extractedDown);
        var migrationFile = Path.Combine(_migrationsFolder, $"{className}.cs");

        await File.WriteAllTextAsync(migrationFile, migrationContent);
        Console.WriteLine($"✅ Created: {Path.GetFileName(migrationFile)}");

        // Create designer file
        var designerContent = GenerateDesignerContent(className, migrationName);
        var designerFile = Path.Combine(_migrationsFolder, $"{className}.Designer.cs");
        await File.WriteAllTextAsync(designerFile, designerContent);
        Console.WriteLine($"✅ Created: {Path.GetFileName(designerFile)}");
        return className;
    }

    private string GenerateConsolidatedMigrationContent(string className, string migrationName, string extractedSchema, string extractedDown)
    {
        var projectName = Path.GetFileNameWithoutExtension(_projectPath);

        return $@"using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace {projectName}.Migrations
{{
    /// <inheritdoc />
    public partial class {migrationName} : Migration
    {{
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {{
{extractedSchema}
        }}

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {{
{extractedDown}
        }}
    }}
}}";
    }

    private string ExtractDownMethodsAsync()
    {
        var migrationFiles = GetMigrationFiles()
            .Where(f => !f.EndsWith(".Designer.cs"))
            .OrderByDescending(f => f) // Reverse order for Down methods
            .ToList();

        var consolidatedDown = new StringBuilder();
        var hasExtractedDowns = false;

        // First pass: try to extract actual Down methods
        foreach (var file in migrationFiles)
        {
            try
            {
                var content = File.ReadAllText(file);
                var downMethodContent = ExtractDownMethodContent(content);

                if (!string.IsNullOrWhiteSpace(downMethodContent))
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    consolidatedDown.AppendLine($"            // From {fileName}");
                    consolidatedDown.AppendLine(downMethodContent);
                    consolidatedDown.AppendLine();
                    hasExtractedDowns = true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Could not extract Down from {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        // If we found actual Down methods, return them
        if (hasExtractedDowns)
        {
            return consolidatedDown.ToString().TrimEnd();
        }

        // Second pass: if no Down methods found, generate inverse operations from Up methods
        var generatedDownOps = new StringBuilder();
        var dropTableOps = new List<string>();

        foreach (var file in migrationFiles)
        {
            try
            {
                var content = File.ReadAllText(file);
                var upMethodContent = ExtractUpMethodContent(content);

                if (!string.IsNullOrWhiteSpace(upMethodContent))
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);

                    // Extract and reverse DropIndex operations to CreateIndex
                    var dropIndexMatches = Regex.Matches(upMethodContent, @"migrationBuilder\.DropIndex\(\s*name:\s*""([^""]+)""[^)]*\);");
                    foreach (Match match in dropIndexMatches)
                    {
                        generatedDownOps.AppendLine($"            // From {fileName} - Recreate dropped index");
                        generatedDownOps.AppendLine($"            migrationBuilder.CreateIndex(");
                        generatedDownOps.AppendLine($"                name: \"{match.Groups[1].Value}\");");
                    }

                    // Extract and reverse DropColumn operations to AddColumn
                    var dropColumnMatches = Regex.Matches(upMethodContent, @"migrationBuilder\.DropColumn\(\s*name:\s*""([^""]+)"",\s*table:\s*""([^""]+)""");
                    foreach (Match match in dropColumnMatches)
                    {
                        generatedDownOps.AppendLine($"            // From {fileName} - Recreate dropped column");
                        generatedDownOps.AppendLine($"            migrationBuilder.AddColumn<string>(");
                        generatedDownOps.AppendLine($"                name: \"{match.Groups[1].Value}\",");
                        generatedDownOps.AppendLine($"                table: \"{match.Groups[2].Value}\",");
                        generatedDownOps.AppendLine($"                type: \"nvarchar(max)\",");
                        generatedDownOps.AppendLine($"                nullable: false,");
                        generatedDownOps.AppendLine($"                defaultValue: \"\");");
                        generatedDownOps.AppendLine();
                    }

                    // Extract CreateTable operations to generate DropTable
                    var createTableMatches = Regex.Matches(upMethodContent, @"migrationBuilder\.CreateTable\(\s*name:\s*""([^""]+)""");
                    foreach (Match match in createTableMatches)
                    {
                        dropTableOps.Add(match.Groups[1].Value);
                    }

                    // Extract CreateIndex operations to generate DropIndex
                    var createIndexMatches = Regex.Matches(upMethodContent, @"migrationBuilder\.CreateIndex\(\s*name:\s*""([^""]+)""");
                    foreach (Match match in createIndexMatches)
                    {
                        generatedDownOps.AppendLine($"            // From {fileName} - Drop created index");
                        generatedDownOps.AppendLine($"            migrationBuilder.DropIndex(");
                        generatedDownOps.AppendLine($"                name: \"{match.Groups[1].Value}\",");
                        generatedDownOps.AppendLine($"                table: \"\");");
                        generatedDownOps.AppendLine();
                    }

                    // Extract AddColumn operations to generate DropColumn
                    var addColumnMatches = Regex.Matches(upMethodContent, @"migrationBuilder\.AddColumn<[^>]*>\(\s*name:\s*""([^""]+)"",\s*table:\s*""([^""]+)""");
                    foreach (Match match in addColumnMatches)
                    {
                        generatedDownOps.AppendLine($"            // From {fileName} - Drop added column");
                        generatedDownOps.AppendLine($"            migrationBuilder.DropColumn(");
                        generatedDownOps.AppendLine($"                name: \"{match.Groups[1].Value}\",");
                        generatedDownOps.AppendLine($"                table: \"{match.Groups[2].Value}\");");
                        generatedDownOps.AppendLine();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Could not process {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        // Add drop table operations at the end (in reverse order)
        if (dropTableOps.Count > 0)
        {
            generatedDownOps.AppendLine("            // Drop tables in reverse order of creation");
            foreach (var tableName in dropTableOps.Reverse<string>())
            {
                generatedDownOps.AppendLine($"            migrationBuilder.DropTable(");
                generatedDownOps.AppendLine($"                name: \"{tableName}\");");
                generatedDownOps.AppendLine();
            }
        }

        return generatedDownOps.Length > 0 ? generatedDownOps.ToString().TrimEnd() : string.Empty;
    }

    private string ExtractDownMethodContent(string migrationContent)
    {
        try
        {
            // Find the Down method
            var downMethodPattern = @"protected\s+override\s+void\s+Down\s*\(\s*MigrationBuilder\s+migrationBuilder\s*\)\s*{";
            var match = Regex.Match(migrationContent, downMethodPattern);

            if (!match.Success) return string.Empty;

            var braceStart = match.Index + match.Length - 1;
            var braceCount = 1;
            var pos = braceStart + 1;

            while (pos < migrationContent.Length && braceCount > 0)
            {
                if (migrationContent[pos] == '{') braceCount++;
                else if (migrationContent[pos] == '}') braceCount--;
                pos++;
            }

            if (braceCount == 0)
            {
                var methodContent = migrationContent.Substring(braceStart + 1, pos - braceStart - 2);
                return methodContent.Trim();
            }
        }
        catch (Exception)
        {
            // Fallback
        }

        return string.Empty;
    }

    private string GenerateTodoSchema()
    {
        return @"            // TODO: Add your table creation commands here
            // 
            // The automatic extraction didn't find schema commands.
            // 
            // To populate this migration:
            // 1. Look in your backup folder (MigrationsBackup_*) 
            // 2. Copy CreateTable commands from your original migrations
            // 3. Or generate a new migration temporarily to see the full schema
            //
            // Example:
            /*
            migrationBuilder.CreateTable(
                name: ""Users"",
                columns: table => new
                {
                    Id = table.Column<int>(type: ""int"", nullable: false)
                        .Annotation(""SqlServer:Identity"", ""1, 1""),
                    Name = table.Column<string>(type: ""nvarchar(max)"", nullable: false),
                    Email = table.Column<string>(type: ""nvarchar(max)"", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey(""PK_Users"", x => x.Id);
                });
            */
            
            throw new NotImplementedException(""Please add your table creation commands to this migration."");";
    }

    private string GenerateDesignerContent(string className, string migrationName)
    {
        var projectName = Path.GetFileNameWithoutExtension(_projectPath);
        var modelSnapshot = GetModelSnapshot();

        // If we have a model snapshot, extract the BuildModel method content
        if (!string.IsNullOrWhiteSpace(modelSnapshot))
        {
            try
            {
                var buildModelMatch = Regex.Match(modelSnapshot, @"protected\s+override\s+void\s+BuildModel\s*\(\s*ModelBuilder\s+modelBuilder\s*\)\s*{");
                if (buildModelMatch.Success)
                {
                    var buildModelContent = ExtractBracedBody(modelSnapshot, buildModelMatch);
                    buildModelContent = Regex.Replace(
                        buildModelContent,
                        @"\.HasAnnotation\(""ProductVersion"",\s*""[^""]+""\)",
                        $@".HasAnnotation(""ProductVersion"", ""{EfCoreVersion}"")");
                    var snapshotHeader = modelSnapshot[..buildModelMatch.Index];
                    var generatedUsings = new HashSet<string>(StringComparer.Ordinal)
                    {
                        "using System;",
                        "using Microsoft.EntityFrameworkCore;",
                        "using Microsoft.EntityFrameworkCore.Infrastructure;",
                        "using Microsoft.EntityFrameworkCore.Metadata;",
                        "using Microsoft.EntityFrameworkCore.Migrations;",
                        "using Microsoft.EntityFrameworkCore.Storage.ValueConversion;"
                    };
                    var usingDirectives = string.Join(Environment.NewLine,
                        snapshotHeader.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                            .Where(line => line.TrimStart().StartsWith("using ", StringComparison.Ordinal))
                            .Where(line => !generatedUsings.Contains(line.Trim()))
                            .Distinct());
                    var namespaceMatch = Regex.Match(snapshotHeader, @"\bnamespace\s+([^\s{;]+)");
                    var migrationNamespace = namespaceMatch.Success
                        ? namespaceMatch.Groups[1].Value
                        : $"{projectName}.Migrations";

                    return $@"// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
{usingDirectives}

#nullable disable

namespace {migrationNamespace}
{{
    [DbContext(typeof({_contextName}))]
    [Migration(""{className}"")]
    partial class {migrationName}
    {{
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {{{buildModelContent}
        }}
    }}
}}";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Could not parse model snapshot: {ex.Message}");
            }
        }

        // Fallback to template
        return $@"// <auto-generated />
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace {projectName}.Migrations
{{
    [DbContext(typeof({_contextName}))]
    [Migration(""{className}"")]
    partial class {migrationName}
    {{
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {{
            // TODO: This should match your current model
            // You can copy this from your model snapshot or latest migration's Designer file
            
            modelBuilder
                .HasAnnotation(""ProductVersion"", ""{EfCoreVersion}"")
                .HasAnnotation(""Relational:MaxIdentifierLength"", 128);

            SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);
                
            // Add your model configuration here
        }}
    }}
}}";
    }

    private static string ExtractBracedBody(string content, Match signatureMatch)
    {
        var braceStart = signatureMatch.Index + signatureMatch.Length - 1;
        var depth = 1;

        for (var position = braceStart + 1; position < content.Length; position++)
        {
            if (content[position] == '{') depth++;
            else if (content[position] == '}') depth--;

            if (depth == 0)
                return content.Substring(braceStart + 1, position - braceStart - 1);
        }

        throw new InvalidDataException("The model snapshot contains an incomplete BuildModel method.");
    }

    private void GenerateDatabaseUpdateScript(string migrationName, string migrationId)
    {
        var scriptContent = $@"-- ===============================================
-- EF Core Migration Squash - Database Update Script
-- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
-- Migration: {migrationName}
-- Author: AmirTahan80
-- ===============================================
--
-- This script updates existing databases after migration squashing.
-- 
-- ⚠️  IMPORTANT INSTRUCTIONS:
-- 1. BACKUP your database before running this script
-- 2. This script is for databases that already have your schema
-- 3. Do NOT run this on new/empty databases
-- 4. Test on a development database first
--
-- ===============================================

PRINT 'Starting EF Core Migration History Update...';
PRINT 'Migration: {migrationName}';
PRINT 'Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC';
PRINT '';

-- Step 1: Check current migration state
PRINT '=== Current Migration History ===';
IF EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '__EFMigrationsHistory')
BEGIN
    SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId;
END
ELSE
BEGIN
    PRINT 'No __EFMigrationsHistory table found. This might be a new database.';
END

PRINT '';
PRINT '=== Updating Migration History ===';

-- Step 2: Clear old migration history (UNCOMMENT AFTER BACKUP!)
-- ⚠️  UNCOMMENT THE NEXT LINE ONLY AFTER YOU'VE BACKED UP YOUR DATABASE
-- DELETE FROM __EFMigrationsHistory WHERE MigrationId LIKE '%[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]_%';

-- Step 3: Add the new consolidated migration as 'applied'
-- This tells EF Core that this migration has already been applied to this database
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) 
VALUES ('{migrationId}', '{EfCoreVersion}');

-- Step 4: Verify the update
PRINT '';
PRINT '=== Updated Migration History ===';
SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId;

PRINT '';
PRINT 'Migration history update completed successfully!';
PRINT 'Your database now recognizes the consolidated migration: {migrationName}';

-- ===============================================
-- VERIFICATION QUERIES
-- ===============================================
-- Run these to verify your database state:

-- Check table count
SELECT COUNT(*) as TableCount FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE';

-- Check migration history
SELECT COUNT(*) as MigrationCount FROM __EFMigrationsHistory;

-- ===============================================
-- NOTES:
-- - The consolidated migration file will handle new database creation
-- - This script only updates the migration tracking for existing databases  
-- - If you encounter issues, restore from backup and open a GitHub issue
-- - Test thoroughly before applying to production!
-- ===============================================
";

        var scriptFile = Path.Combine(_migrationsFolder, "UpdateExistingDatabases.sql");
        File.WriteAllText(scriptFile, scriptContent);

        Console.WriteLine($"📄 Generated database update script: {Path.GetFileName(scriptFile)}");
    }
}
