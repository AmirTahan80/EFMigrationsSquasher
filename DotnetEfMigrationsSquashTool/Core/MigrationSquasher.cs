using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text;
using System.Text.RegularExpressions;
using EfMigrationSquasher.Core;

public class MigrationSquasher
{
    private const string DefaultEfCoreVersion = "10.0.3";
    private readonly string _projectPath;
    private readonly string _contextName;
    private readonly string _migrationsFolder;
    private readonly string _projectDirectory;

    public MigrationSquasher(string projectPath, string contextName, string migrationsFolder)
    {
        _projectPath = Path.GetFullPath(projectPath);
        _contextName = contextName;
        _projectDirectory = Path.GetDirectoryName(_projectPath)!;
        _migrationsFolder = Path.GetFullPath(migrationsFolder);
    }

    public async Task PreviewSquashAsync(string? newMigrationName, bool optimize = false)
    {
        var migrations = GetExistingMigrationFiles();
        if (migrations.Count == 0)
        {
            Console.WriteLine("❌ No migrations found to squash!");
            return;
        }

        Console.WriteLine($"📋 Found {migrations.Count} existing migrations:");
        foreach (var migration in migrations)
        {
            Console.WriteLine($"  • {Path.GetFileNameWithoutExtension(migration)}");
        }

        bool isMergeIntoLast = string.IsNullOrWhiteSpace(newMigrationName);
        if (isMergeIntoLast)
        {
            var lastMigration = Path.GetFileNameWithoutExtension(migrations.Last());
            Console.WriteLine($"\n🎯 Target: Merge all migrations into LAST migration '{lastMigration}'");
            Console.WriteLine("   (Preserves migration ID in __EFMigrationsHistory; zero database impact on existing DBs)");
        }
        else
        {
            Console.WriteLine($"\n🎯 Target: Create new migration '{newMigrationName}'");
        }

        Console.WriteLine($"📁 Migrations folder: {_migrationsFolder}");

        var filesToRemove = GetFilesToRemove(newMigrationName);
        Console.WriteLine($"\n📄 Files that would be removed ({filesToRemove.Count}):");
        foreach (var file in filesToRemove)
        {
            Console.WriteLine($"  • {Path.GetFileName(file)}");
        }

        if (optimize && migrations.Count > 0)
        {
            var parsedMigrations = new List<ParsedMigration>();
            foreach (var file in migrations)
            {
                var code = await File.ReadAllTextAsync(file);
                parsedMigrations.Add(MigrationParser.ParseMigration(code, file));
            }
            var optResult = OperationOptimizer.Optimize(parsedMigrations);
            PrintOptimizationReport(optResult);
        }

        Console.WriteLine("\n💡 To actually perform the squash, run without --dry-run");
        Console.WriteLine("⚠️  Make sure to backup your project first!");
        await Task.CompletedTask;
    }

    public async Task SquashMigrationsAsync(string? migrationName, bool optimize = false)
    {
        try
        {
            Console.WriteLine("🚀 Starting migration squash process...");
            Console.WriteLine();

            // Step 1: Validate migrations exist
            var migrationFiles = GetExistingMigrationFiles();
            if (migrationFiles.Count == 0)
            {
                Console.WriteLine("❌ No migrations found to squash!");
                return;
            }

            Console.WriteLine($"📋 Found {migrationFiles.Count} migrations to squash");

            bool isMergeIntoLast = string.IsNullOrWhiteSpace(migrationName);

            // Step 2: Create backup
            await CreateBackupAsync();

            // Step 3: Get current model snapshot
            var (snapshotContent, snapshotPath) = GetModelSnapshot();
            var parsedSnapshot = !string.IsNullOrWhiteSpace(snapshotContent)
                ? MigrationParser.ParseModelSnapshot(snapshotContent)
                : new ParsedModelSnapshot { EfCoreVersion = DefaultEfCoreVersion };

            var efCoreVersion = parsedSnapshot.EfCoreVersion ?? DefaultEfCoreVersion;
            var migrationNamespace = parsedSnapshot.Namespace
                ?? $"{Path.GetFileNameWithoutExtension(_projectPath)}.Migrations";

            Console.WriteLine($"📦 EF Core product version: {efCoreVersion}");

            // Step 4: Parse all migrations with Roslyn AST
            var parsedMigrations = new List<ParsedMigration>();
            foreach (var file in migrationFiles)
            {
                var code = await File.ReadAllTextAsync(file);
                var parsed = MigrationParser.ParseMigration(code, file);
                parsedMigrations.Add(parsed);
                Console.WriteLine($"  📄 Parsed: {Path.GetFileNameWithoutExtension(file)}");
            }

            if (optimize)
            {
                var optResult = OperationOptimizer.Optimize(parsedMigrations);
                PrintOptimizationReport(optResult);
            }

            // Step 5: Remove old migration files safely
            RemoveOldMigrations(migrationName);

            // Step 6: Create or overwrite consolidated migration with both Up and Down
            string targetMigrationId;
            string targetClassName;

            if (isMergeIntoLast)
            {
                var lastMigration = parsedMigrations.Last();
                targetMigrationId = lastMigration.MigrationId;
                targetClassName = lastMigration.ClassName;
                Console.WriteLine($"📝 Merging all migrations into last migration: {targetMigrationId}");
            }
            else
            {
                var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                targetMigrationId = $"{timestamp}_{migrationName}";
                targetClassName = migrationName!;
                Console.WriteLine($"📝 Creating consolidated migration: {migrationName}");
            }

            var (migrationContent, designerContent) = GenerateMigrationAndDesigner(
                targetClassName,
                targetMigrationId,
                parsedMigrations,
                parsedSnapshot,
                efCoreVersion,
                migrationNamespace);

            var migrationFile = Path.Combine(_migrationsFolder, $"{targetMigrationId}.cs");
            await File.WriteAllTextAsync(migrationFile, migrationContent);
            Console.WriteLine($"✅ Saved: {Path.GetFileName(migrationFile)}");

            var designerFile = Path.Combine(_migrationsFolder, $"{targetMigrationId}.Designer.cs");
            await File.WriteAllTextAsync(designerFile, designerContent);
            Console.WriteLine($"✅ Saved: {Path.GetFileName(designerFile)}");

            // Step 7: Generate database update / cleanup script
            if (isMergeIntoLast)
            {
                var earlierMigrations = parsedMigrations.Take(parsedMigrations.Count - 1).ToList();
                GenerateHistoryCleanupScript(targetMigrationId, earlierMigrations, efCoreVersion);
            }
            else
            {
                GenerateDatabaseUpdateScript(migrationName!, targetMigrationId, efCoreVersion);
            }

            Console.WriteLine();
            Console.WriteLine("✅ Migration squash completed successfully!");
            Console.WriteLine();
            Console.WriteLine("📋 Next steps:");
            if (isMergeIntoLast)
            {
                Console.WriteLine("1. Review the merged migration");
                Console.WriteLine("2. Existing databases already recognize this migration (zero DB changes will be applied)");
                Console.WriteLine("3. Optionally run the SQL script to clean up folded-away migration IDs from __EFMigrationsHistory");
            }
            else
            {
                Console.WriteLine("1. Review the generated consolidated migration");
                Console.WriteLine("2. Update existing databases using the generated SQL script");
                Console.WriteLine("3. Test thoroughly before deploying to production");
            }
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

    public List<string> GetExistingMigrationFiles()
    {
        if (!Directory.Exists(_migrationsFolder))
            return new List<string>();

        return Directory.GetFiles(_migrationsFolder, "*.cs")
            .Where(f => !f.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^\d{14}_.*\.cs$"))
            .OrderBy(f => Path.GetFileName(f))
            .ToList();
    }

    public List<string> GetFilesToRemove(string? newMigrationName = null)
    {
        var migrationFiles = GetExistingMigrationFiles();
        var filesToRemove = new List<string>();

        // If merging into last migration, keep the last migration file (it will be overwritten)
        bool isMergeIntoLast = string.IsNullOrWhiteSpace(newMigrationName);
        var filesToProcess = isMergeIntoLast && migrationFiles.Count > 0
            ? migrationFiles.Take(migrationFiles.Count - 1).ToList()
            : migrationFiles;

        foreach (var migrationFile in filesToProcess)
        {
            filesToRemove.Add(migrationFile);

            var dir = Path.GetDirectoryName(migrationFile)!;
            var nameWithoutExt = Path.GetFileNameWithoutExtension(migrationFile);
            var designerFile = Path.Combine(dir, $"{nameWithoutExt}.Designer.cs");

            if (File.Exists(designerFile))
            {
                filesToRemove.Add(designerFile);
            }
        }

        return filesToRemove;
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
            var backupPath = Path.Combine(backupFolder, $"{fileName}.bak");
            File.Copy(file, backupPath, overwrite: true);
        }

        Console.WriteLine($"✅ Backup created with {Directory.GetFiles(backupFolder).Length} files");
        await Task.CompletedTask;
    }

    private (string Content, string? FilePath) GetModelSnapshot()
    {
        if (!Directory.Exists(_migrationsFolder))
            return (string.Empty, null);

        var contextSnapshotPattern = $"*{_contextName}ModelSnapshot.cs";
        var contextSnapshot = Directory.GetFiles(_migrationsFolder, contextSnapshotPattern).FirstOrDefault();

        if (contextSnapshot != null && File.Exists(contextSnapshot))
        {
            Console.WriteLine($"📸 Found model snapshot matching context: {Path.GetFileName(contextSnapshot)}");
            return (File.ReadAllText(contextSnapshot), contextSnapshot);
        }

        var allSnapshots = Directory.GetFiles(_migrationsFolder, "*ModelSnapshot.cs");
        if (allSnapshots.Length == 1)
        {
            Console.WriteLine($"📸 Found model snapshot: {Path.GetFileName(allSnapshots[0])}");
            return (File.ReadAllText(allSnapshots[0]), allSnapshots[0]);
        }
        else if (allSnapshots.Length > 1)
        {
            var names = string.Join(", ", allSnapshots.Select(Path.GetFileName));
            throw new InvalidOperationException(
                $"Multiple model snapshots found ({names}). Please specify the exact --context name that matches your snapshot.");
        }

        Console.WriteLine("⚠️  No model snapshot found");
        return (string.Empty, null);
    }

    private void RemoveOldMigrations(string? newMigrationName = null)
    {
        Console.WriteLine("🗑️  Removing old migration files...");

        var filesToRemove = GetFilesToRemove(newMigrationName);
        foreach (var file in filesToRemove)
        {
            Console.WriteLine($"   Removing: {Path.GetFileName(file)}");
            File.Delete(file);
        }

        Console.WriteLine($"✅ Removed {filesToRemove.Count} migration files (preserved custom code and snapshots)");
    }

    public (string MigrationContent, string DesignerContent) GenerateMigrationAndDesigner(
        string targetClassName,
        string targetMigrationId,
        List<ParsedMigration> parsedMigrations,
        ParsedModelSnapshot parsedSnapshot,
        string efCoreVersion,
        string migrationNamespace)
    {
        // 1. Build Up method content
        var consolidatedUp = new StringBuilder();
        int stepIndex = 1;
        foreach (var migration in parsedMigrations)
        {
            if (string.IsNullOrWhiteSpace(migration.UpBody))
                continue;

            consolidatedUp.AppendLine($"            // From {migration.MigrationId}");
            if (migration.UpHasReturnStatement)
            {
                // Wrap in local function to prevent early return from terminating entire migration
                consolidatedUp.AppendLine("            {");
                consolidatedUp.AppendLine($"                void Step_{stepIndex}()");
                consolidatedUp.AppendLine("                {");
                consolidatedUp.AppendLine(Indent(migration.UpBody, 20));
                consolidatedUp.AppendLine("                }");
                consolidatedUp.AppendLine($"                Step_{stepIndex}();");
                consolidatedUp.AppendLine("            }");
            }
            else
            {
                consolidatedUp.AppendLine("            {");
                consolidatedUp.AppendLine(Indent(migration.UpBody, 16));
                consolidatedUp.AppendLine("            }");
            }
            consolidatedUp.AppendLine();
            stepIndex++;
        }

        var upBody = consolidatedUp.Length > 0
            ? consolidatedUp.ToString().TrimEnd()
            : "            // No schema commands found in previous migrations.";

        // 2. Build Down method content
        var consolidatedDown = new StringBuilder();
        var hasExplicitDown = parsedMigrations.Any(m => !string.IsNullOrWhiteSpace(m.DownBody));

        if (hasExplicitDown)
        {
            int downStepIndex = 1;
            // Down methods in reverse chronological order
            foreach (var migration in parsedMigrations.AsEnumerable().Reverse())
            {
                if (string.IsNullOrWhiteSpace(migration.DownBody))
                    continue;

                consolidatedDown.AppendLine($"            // From {migration.MigrationId}");
                if (migration.DownHasReturnStatement)
                {
                    consolidatedDown.AppendLine("            {");
                    consolidatedDown.AppendLine($"                void Step_Down_{downStepIndex}()");
                    consolidatedDown.AppendLine("                {");
                    consolidatedDown.AppendLine(Indent(migration.DownBody, 20));
                    consolidatedDown.AppendLine("                }");
                    consolidatedDown.AppendLine($"                Step_Down_{downStepIndex}();");
                    consolidatedDown.AppendLine("            }");
                }
                else
                {
                    consolidatedDown.AppendLine("            {");
                    consolidatedDown.AppendLine(Indent(migration.DownBody, 16));
                    consolidatedDown.AppendLine("            }");
                }
                consolidatedDown.AppendLine();
                downStepIndex++;
            }
        }
        else
        {
            // Synthesize safe reverse operations from Up
            var nonDropTableOps = new List<string>();
            var dropTableOps = new List<string>();

            foreach (var migration in parsedMigrations.AsEnumerable().Reverse())
            {
                foreach (var op in migration.SynthesizedDownOperations)
                {
                    if (op.IsDropTable)
                    {
                        dropTableOps.Add(op.Code);
                    }
                    else
                    {
                        nonDropTableOps.Add(op.Code);
                    }
                }
            }

            foreach (var op in nonDropTableOps)
            {
                consolidatedDown.AppendLine($"            {op}");
            }

            if (dropTableOps.Count > 0)
            {
                consolidatedDown.AppendLine("            // Drop tables in reverse order of creation");
                foreach (var op in dropTableOps)
                {
                    consolidatedDown.AppendLine($"            {op}");
                }
            }
        }

        var downBody = consolidatedDown.Length > 0
            ? consolidatedDown.ToString().TrimEnd()
            : "            // No rollback commands found.";

        // 3. Collect extra class members (helper methods, constants, etc.)
        var extraMembersCode = new StringBuilder();
        var seenMembers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var migration in parsedMigrations)
        {
            foreach (var member in migration.ExtraMembers)
            {
                if (seenMembers.Add(member))
                {
                    extraMembersCode.AppendLine();
                    extraMembersCode.AppendLine(Indent(member, 8));
                }
            }
        }

        // 4. Collect using directives
        var usingDirectives = new HashSet<string>(StringComparer.Ordinal)
        {
            "using System;",
            "using Microsoft.EntityFrameworkCore.Migrations;"
        };

        foreach (var migration in parsedMigrations)
        {
            foreach (var u in migration.Usings)
            {
                usingDirectives.Add(u);
            }
        }

        var usingsBlock = string.Join(Environment.NewLine, usingDirectives.OrderBy(u => u));

        // 5. Generate Migration content
        var migrationContent = $@"{usingsBlock}

#nullable disable

namespace {migrationNamespace}
{{
    /// <inheritdoc />
    public partial class {targetClassName} : Migration
    {{
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {{
{upBody}
        }}

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {{
{downBody}
        }}{extraMembersCode}
    }}
}}";

        // 6. Generate Designer content
        var designerUsings = new HashSet<string>(StringComparer.Ordinal)
        {
            "using System;",
            "using Microsoft.EntityFrameworkCore;",
            "using Microsoft.EntityFrameworkCore.Infrastructure;",
            "using Microsoft.EntityFrameworkCore.Metadata;",
            "using Microsoft.EntityFrameworkCore.Migrations;",
            "using Microsoft.EntityFrameworkCore.Storage.ValueConversion;"
        };

        if (parsedSnapshot.Usings != null)
        {
            foreach (var u in parsedSnapshot.Usings)
            {
                designerUsings.Add(u);
            }
        }

        var designerUsingsBlock = string.Join(Environment.NewLine, designerUsings.OrderBy(u => u));

        string targetModelContent;
        if (!string.IsNullOrWhiteSpace(parsedSnapshot.BuildModelBody))
        {
            targetModelContent = Indent(parsedSnapshot.BuildModelBody, 12);
        }
        else
        {
            targetModelContent = $@"            modelBuilder
                .HasAnnotation(""ProductVersion"", ""{efCoreVersion}"")
                .HasAnnotation(""Relational:MaxIdentifierLength"", 128);";
        }

        var designerContent = $@"// <auto-generated />
{designerUsingsBlock}

#nullable disable

namespace {migrationNamespace}
{{
    [DbContext(typeof({_contextName}))]
    [Migration(""{targetMigrationId}"")]
    partial class {targetClassName}
    {{
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {{
{targetModelContent}
        }}
    }}
}}";

        return (migrationContent, designerContent);
    }

    private static string Indent(string code, int spaces)
    {
        var indent = new string(' ', spaces);
        var lines = code.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        return string.Join(Environment.NewLine, lines.Select(line => string.IsNullOrWhiteSpace(line) ? string.Empty : $"{indent}{line.TrimEnd()}"));
    }

    private void GenerateHistoryCleanupScript(string keptMigrationId, List<ParsedMigration> removedMigrations, string efCoreVersion)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- ===============================================");
        sb.AppendLine("-- EF Core Migration Merge - History Cleanup Script");
        sb.AppendLine($"-- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"-- Kept migration (already applied): {keptMigrationId}");
        sb.AppendLine("-- Author: AmirTahan80");
        sb.AppendLine("-- ===============================================");
        sb.AppendLine("--");
        sb.AppendLine("-- This script removes older folded-away migration IDs from __EFMigrationsHistory.");
        sb.AppendLine("-- The database schema is ALREADY UP TO DATE because the last migration ID was kept.");
        sb.AppendLine("-- Databases that already have this migration ID will NOT re-apply any schema changes.");
        sb.AppendLine("--");
        sb.AppendLine("-- ⚠️  BACKUP your database before running this script.");
        sb.AppendLine("-- ===============================================");
        sb.AppendLine();
        sb.AppendLine("PRINT 'Starting EF Core migration history cleanup...';");
        sb.AppendLine();
        sb.AppendLine("-- Step 1: Review current history");
        sb.AppendLine("SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId;");
        sb.AppendLine();

        if (removedMigrations.Count > 0)
        {
            sb.AppendLine("-- Step 2: Remove the folded-away migration IDs (the kept migration stays).");
            sb.AppendLine("-- Uncomment after backup:");
            sb.AppendLine("-- DELETE FROM __EFMigrationsHistory");
            sb.AppendLine("-- WHERE MigrationId IN (");
            for (int i = 0; i < removedMigrations.Count; i++)
            {
                var comma = i < removedMigrations.Count - 1 ? "," : "";
                sb.AppendLine($"--   '{removedMigrations[i].MigrationId}'{comma}");
            }
            sb.AppendLine("-- );");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("-- Nothing to remove: only one migration existed.");
            sb.AppendLine();
        }

        sb.AppendLine("-- Step 3: Verify (the kept migration should remain)");
        sb.AppendLine("SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId;");
        sb.AppendLine();
        sb.AppendLine("PRINT 'History cleanup completed. Schema unchanged.';");
        sb.AppendLine();
        sb.AppendLine("-- ===============================================");
        sb.AppendLine("-- NON-SQL SERVER PROVIDERS (PostgreSQL, SQLite, MySQL)");
        sb.AppendLine("-- ===============================================");
        if (removedMigrations.Count > 0)
        {
            var idList = string.Join(", ", removedMigrations.Select(m => $"'{m.MigrationId}'"));
            sb.AppendLine($"-- PostgreSQL / SQLite: DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" IN ({idList});");
            sb.AppendLine($"-- MySQL / MariaDB:    DELETE FROM `__EFMigrationsHistory` WHERE `MigrationId` IN ({idList});");
        }
        sb.AppendLine("-- ===============================================");

        var scriptFile = Path.Combine(_migrationsFolder, "UpdateExistingDatabases.sql");
        File.WriteAllText(scriptFile, sb.ToString());
        Console.WriteLine($"📄 Generated history cleanup script: {Path.GetFileName(scriptFile)}");
    }

    private void GenerateDatabaseUpdateScript(string migrationName, string migrationId, string efCoreVersion)
    {
        var scriptContent = $@"-- ===============================================
-- EF Core Migration Squash - Database Update Script
-- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
-- Migration: {migrationName}
-- Migration ID: {migrationId}
-- EF Core Version: {efCoreVersion}
-- ===============================================
--
-- This script updates existing databases after migration squashing.
-- 
-- ⚠️  IMPORTANT INSTRUCTIONS:
-- 1. BACKUP your database before running this script
-- 2. This script is for databases that already have your schema
-- 3. Do NOT run this on new/empty databases
-- 4. Test on a development/staging database first
--
-- ===============================================

PRINT 'Starting EF Core Migration History Update...';
PRINT 'Migration: {migrationName}';
PRINT 'MigrationId: {migrationId}';
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

-- Step 2: Clear old migration history for this DbContext (UNCOMMENT AFTER BACKUP!)
-- ⚠️  UNCOMMENT THE NEXT LINE ONLY AFTER YOU'VE BACKED UP YOUR DATABASE
-- DELETE FROM __EFMigrationsHistory WHERE MigrationId <> '{migrationId}';

-- Step 3: Add the new consolidated migration as 'applied'
-- This tells EF Core that this migration has already been applied to this database
IF NOT EXISTS (SELECT 1 FROM __EFMigrationsHistory WHERE MigrationId = '{migrationId}')
BEGIN
    INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) 
    VALUES ('{migrationId}', '{efCoreVersion}');
    PRINT 'Added consolidated migration {migrationId} to __EFMigrationsHistory.';
END
ELSE
BEGIN
    PRINT 'Consolidated migration {migrationId} is already recorded in __EFMigrationsHistory.';
END

-- Step 4: Verify the update
PRINT '';
PRINT '=== Updated Migration History ===';
SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId;

PRINT '';
PRINT 'Migration history update completed successfully!';
PRINT 'Your database now recognizes the consolidated migration: {migrationName}';

-- ===============================================
-- NON-SQL SERVER PROVIDERS (PostgreSQL, SQLite, MySQL)
-- ===============================================
-- PostgreSQL:
--   INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
--   VALUES ('{migrationId}', '{efCoreVersion}')
--   ON CONFLICT (""MigrationId"") DO NOTHING;
--
-- SQLite:
--   INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
--   VALUES ('{migrationId}', '{efCoreVersion}');
--
-- MySQL / MariaDB:
--   INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
--   VALUES ('{migrationId}', '{efCoreVersion}');
-- ===============================================
";

        var scriptFile = Path.Combine(_migrationsFolder, "UpdateExistingDatabases.sql");
        File.WriteAllText(scriptFile, scriptContent);

        Console.WriteLine($"📄 Generated database update script: {Path.GetFileName(scriptFile)}");
    }

    private static void PrintOptimizationReport(OptimizationResult result)
    {
        Console.WriteLine("\n⚡ Optimization Report:");
        if (result.PrunedMessages.Count == 0 && result.RetainedMessages.Count == 0)
        {
            Console.WriteLine("  • No redundant operations found to prune.");
            return;
        }

        foreach (var msg in result.PrunedMessages)
        {
            Console.WriteLine($"  ✅ {msg}");
        }

        foreach (var msg in result.RetainedMessages)
        {
            Console.WriteLine($"  🛡️  {msg}");
        }
    }
}
