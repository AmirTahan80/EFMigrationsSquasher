using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EfMigrationSquasher
{
    /// <summary>
    /// Merges an EF Core project's timestamped migrations into its LAST existing migration.
    /// The last migration keeps its original id/timestamp, so databases that already have that
    /// id recorded in __EFMigrationsHistory are never affected. All earlier migrations are folded
    /// into it and then removed.
    /// </summary>
    public class MigrationSquasher
    {
        private const string DefaultEfCoreVersion = "10.0.3";
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

        // ---- Migration discovery -------------------------------------------------

        private record MigrationInfo(string Id, string Name, string FilePath, string? DesignerPath)
        {
            // Id looks like "20251130215721_initialSchema" (timestamp + name, also the class name).
            public string Timestamp => Id.Length >= 14 ? Id.Substring(0, 14) : Id;
            public string ClassName => Id;
        }

        private List<MigrationInfo> GetExistingMigrations()
        {
            if (!Directory.Exists(_migrationsFolder))
                return new List<MigrationInfo>();

            return Directory.GetFiles(_migrationsFolder, "*.cs")
                .Where(f => !f.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
                .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal))
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name != null && name.Length > 14 && name.Substring(0, 14).All(char.IsDigit))
                .OrderBy(x => x, StringComparer.Ordinal) // chronological
                .Select(id =>
                {
                    var file = Directory.GetFiles(_migrationsFolder, id + ".cs").First();
                    var designer = Directory.GetFiles(_migrationsFolder, id + ".Designer.cs").FirstOrDefault();
                    var name = id.Length > 15 ? id.Substring(15) : id;
                    return new MigrationInfo(id!, name, file, designer);
                })
                .ToList();
        }

        private List<string> GetAllMigrationFiles() =>
            Directory.Exists(_migrationsFolder)
                ? Directory.GetFiles(_migrationsFolder, "*.cs")
                    .Where(f => !f.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
                    .ToList()
                : new List<string>();

        // ---- Preview -------------------------------------------------------------

        public async Task PreviewSquashAsync(bool updateDatabase)
        {
            var migrations = GetExistingMigrations();
            if (migrations.Count == 0)
            {
                Console.WriteLine("❌ No migrations found to squash!");
                return;
            }

            Console.WriteLine($"📋 Found {migrations.Count} existing migrations:");
            foreach (var m in migrations)
                Console.WriteLine($"  • {m.Id}");

            var last = migrations[^1];
            var merged = migrations.Take(migrations.Count - 1).ToList();

            Console.WriteLine();
            Console.WriteLine($"🎯 Will MERGE all migrations into the LAST one: {last.Id}");
            Console.WriteLine("   (its id is preserved so existing databases are not affected)");
            if (merged.Count > 0)
            {
                Console.WriteLine($"🗑️  Will REMOVE {merged.Count} earlier migration(s):");
                foreach (var m in merged)
                    Console.WriteLine($"  • {m.Id}");
            }
            else
            {
                Console.WriteLine("   (only one migration exists — nothing to merge)");
            }

            if (updateDatabase)
                Console.WriteLine("\n💾 Database will be updated (dotnet ef database update) BEFORE squashing.");

            Console.WriteLine("\n💡 To actually perform the squash, run without --dry-run");
            Console.WriteLine("⚠️  Make sure to back up your project / database first!");
            await Task.CompletedTask;
        }

        // ---- Main squash ---------------------------------------------------------

        public async Task<int> SquashMigrationsAsync(string _unusedName, bool updateDatabase)
        {
            try
            {
                Console.WriteLine("🚀 Starting migration merge (into last existing migration)...");
                Console.WriteLine();

                var migrations = GetExistingMigrations();
                if (migrations.Count == 0)
                {
                    Console.WriteLine("❌ No migrations found to squash!");
                    return 1;
                }

                Console.WriteLine($"📋 Found {migrations.Count} migrations");

                // Step 1 (optional): bring the database fully up to date BEFORE we touch files.
                if (updateDatabase)
                {
                    var ok = await UpdateDatabaseAsync();
                    if (!ok) return 1;
                }

                // Step 2: backup
                await CreateBackupAsync();

                // Step 3: metadata from the model snapshot
                var modelSnapshot = GetModelSnapshot();
                var efCoreVersion = GetEfCoreVersion(modelSnapshot);
                var migrationNamespace = GetMigrationNamespace(modelSnapshot);
                var migrationUsingDirectives = GetMigrationUsingDirectives();

                Console.WriteLine($"📦 EF Core product version: {efCoreVersion}");

                var last = migrations[^1];
                var earlier = migrations.Take(migrations.Count - 1).ToList();

                // Step 4: merge Up (chronological) and Down (reverse) with dedup/cancel.
                var (upBody, downBody, mergedCount, deduped, cancelled) =
                    MergeOperations(migrations);

                Console.WriteLine($"🔀 Merged {mergedCount} migration(s) into '{last.Id}'");
                if (deduped > 0) Console.WriteLine($"   removed {deduped} exact-duplicate operation(s)");
                if (cancelled > 0) Console.WriteLine($"   cancelled {cancelled} create/drop pair(s) that net to nothing");

                // Step 5: rewrite the LAST migration file in place (keep its id/name).
                await WriteMergedMigrationAsync(last, upBody, downBody, migrationNamespace, migrationUsingDirectives);

                // Step 6: regenerate the last migration's designer from the snapshot (keeps its id).
                await WriteDesignerAsync(last, modelSnapshot, efCoreVersion, migrationNamespace);

                // Step 7: delete the earlier migration files + their designers.
                foreach (var m in earlier)
                {
                    Console.WriteLine($"🗑️  Removing: {Path.GetFileName(m.FilePath)}");
                    File.Delete(m.FilePath);
                    if (m.DesignerPath != null)
                    {
                        Console.WriteLine($"🗑️  Removing: {Path.GetFileName(m.DesignerPath)}");
                        File.Delete(m.DesignerPath);
                    }
                }

                // Step 8: history cleanup script (delete only the folded-away ids).
                GenerateHistoryCleanupScript(last, earlier, efCoreVersion);

                Console.WriteLine();
                Console.WriteLine("✅ Migration merge completed successfully!");
                Console.WriteLine();
                Console.WriteLine("📋 Next steps:");
                Console.WriteLine("1. Build the project and review the merged migration.");
                Console.WriteLine("2. If you updated the database first, run the generated SQL to drop the");
                Console.WriteLine("   obsolete ids from __EFMigrationsHistory (the last id is kept).");
                Console.WriteLine("3. Test against a disposable database before production.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error during migration merge: {ex.Message}");
                Console.WriteLine("🔄 Restore from backup if needed");
                return 1;
            }
        }

        // ---- Database update (optional) -----------------------------------------

        private async Task<bool> UpdateDatabaseAsync()
        {
            Console.WriteLine("💾 Updating database (dotnet ef database update) ...");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"ef database update --project \"{_projectPath}\" --context {_contextName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("Could not start 'dotnet'.");

                var stdout = await proc.StandardOutput.ReadToEndAsync();
                var stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();

                Console.Write(stdout);
                if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.Write(stderr);

                if (proc.ExitCode != 0)
                {
                    Console.WriteLine("❌ 'dotnet ef database update' failed. Aborting so the database is not left inconsistent.");
                    return false;
                }

                Console.WriteLine("✅ Database is up to date.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Could not update the database: {ex.Message}");
                Console.WriteLine("   Make sure the EF Core tools are installed (dotnet tool install --global dotnet-ef).");
                return false;
            }
        }

        // ---- Backup --------------------------------------------------------------

        private async Task CreateBackupAsync()
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupFolder = Path.Combine(_projectDirectory, $"MigrationsBackup_{timestamp}");

            Console.WriteLine($"💾 Creating backup in: {backupFolder}");
            Directory.CreateDirectory(backupFolder);

            foreach (var file in Directory.GetFiles(_migrationsFolder, "*.cs"))
            {
                var fileName = Path.GetFileName(file);
                // Keep backups outside the SDK's default **/*.cs compile glob via .bak suffix.
                var backupPath = Path.Combine(backupFolder, $"{fileName}.bak");
                File.Copy(file, backupPath);
            }

            Console.WriteLine($"✅ Backup created with {Directory.GetFiles(backupFolder).Length} files");
            await Task.CompletedTask;
        }

        // ---- Model snapshot helpers ---------------------------------------------

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

        private static string GetEfCoreVersion(string modelSnapshot)
        {
            var match = Regex.Match(modelSnapshot,
                @"\.HasAnnotation\(""ProductVersion"",\s*""([^""]+)""\)");
            return match.Success ? match.Groups[1].Value : DefaultEfCoreVersion;
        }

        private string GetMigrationNamespace(string modelSnapshot)
        {
            var match = Regex.Match(modelSnapshot, @"\bnamespace\s+([^\s{;]+)");
            return match.Success
                ? match.Groups[1].Value
                : $"{Path.GetFileNameWithoutExtension(_projectPath)}.Migrations";
        }

        private IReadOnlyList<string> GetMigrationUsingDirectives()
        {
            var usingDirectives = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in GetAllMigrationFiles()
                         .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal))
                         .OrderBy(f => f))
            {
                var content = File.ReadAllText(file);
                var nsMatch = Regex.Match(content, @"\bnamespace\b");
                var header = nsMatch.Success ? content[..nsMatch.Index] : content;

                foreach (var line in header.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                {
                    var directive = line.Trim();
                    if (directive.StartsWith("using ", StringComparison.Ordinal)
                        && directive.EndsWith(';')
                        && seen.Add(directive))
                    {
                        usingDirectives.Add(directive);
                    }
                }
            }
            return usingDirectives;
        }

        // ---- Operation merging ---------------------------------------------------

        private record MigrationOp(string Text, string Type, string Name, string Table)
        {
            public string Key => $"{Name}|{Table}";
        }

        private static readonly HashSet<string> DropTypes = new()
        {
            "DropTable", "DropIndex", "DropColumn", "DropForeignKey", "DropPrimaryKey", "DropCheckConstraint", "DropUniqueConstraint"
        };
        private static readonly HashSet<string> CreateTypes = new()
        {
            "CreateTable", "CreateIndex", "AddColumn", "AddForeignKey", "AddPrimaryKey", "CreateCheckConstraint", "AddUniqueConstraint"
        };

        private static string? FamilyOf(string type) => type switch
        {
            "CreateTable" or "DropTable" => "table",
            "CreateIndex" or "DropIndex" => "index",
            "AddColumn" or "DropColumn" => "column",
            "AddForeignKey" or "DropForeignKey" => "fk",
            "AddPrimaryKey" or "DropPrimaryKey" => "pk",
            "AddUniqueConstraint" or "DropUniqueConstraint" => "uc",
            "CreateCheckConstraint" or "DropCheckConstraint" => "cc",
            _ => null
        };

        private static List<MigrationOp> SplitOperations(string body)
        {
            var ops = new List<MigrationOp>();
            int i = 0, n = body.Length;
            const string marker = "migrationBuilder.";
            while (i < n)
            {
                int idx = body.IndexOf(marker, i, StringComparison.Ordinal);
                if (idx < 0) break;
                int p = body.IndexOf('(', idx);
                if (p < 0) break;
                string type = body.Substring(idx + marker.Length, p - (idx + marker.Length)).Trim();
                int depth = 0, j = p;
                for (; j < n; j++)
                {
                    char c = body[j];
                    if (c is '(' or '{') depth++;
                    else if (c is ')' or '}') depth--;
                    else if (c == ';' && depth == 0) break;
                }
                if (j >= n) break;
                string text = body.Substring(idx, j - idx + 1);
                var nameM = Regex.Match(text, @"name:\s*""([^""]+)""");
                var tableM = Regex.Match(text, @"table:\s*""([^""]+)""");
                ops.Add(new MigrationOp(
                    text,
                    type,
                    nameM.Success ? nameM.Groups[1].Value : "",
                    tableM.Success ? tableM.Groups[1].Value : ""));
                i = j + 1;
            }
            return ops;
        }

        /// <summary>
        /// Merges Up bodies (chronological) and Down bodies (reverse) from all migrations,
        /// removing exact-duplicate operations and cancelling create/drop pairs that net to
        /// nothing (e.g. CreateTable(X) then DropTable(X) leaves X absent → both removed).
        /// </summary>
        private (string Up, string Down, int Merged, int Deduped, int Cancelled) MergeOperations(
            List<MigrationInfo> migrations)
        {
            int deduped = 0, cancelled = 0;

            // ----- UP: chronological -----
            var upAcc = new List<(string Source, MigrationOp Op)>();
            foreach (var m in migrations)
            {
                var content = File.ReadAllText(m.FilePath);
                var upBody = ExtractMethodBody(content, "Up");
                foreach (var op in SplitOperations(upBody))
                    AddOp(upAcc, m.Id, op, ref deduped, ref cancelled);
            }

            // ----- DOWN: reverse chronological -----
            // Apply exact-duplicate removal AND create/drop cancellation, same as Up.
            // A create-then-drop of the same object inside Down is a no-op, so removing
            // both keeps the rollback correct and clean.
            var downAcc = new List<(string Source, MigrationOp Op)>();
            foreach (var m in migrations.AsEnumerable().Reverse())
            {
                var content = File.ReadAllText(m.FilePath);
                var downBody = ExtractMethodBody(content, "Down");
                foreach (var op in SplitOperations(downBody))
                    AddOp(downAcc, m.Id, op, ref deduped, ref cancelled);
            }

            return (RenderBody(upAcc), RenderBody(downAcc), migrations.Count, deduped, cancelled);
        }

        private static void AddOp(
            List<(string Source, MigrationOp Op)> acc, string source, MigrationOp op,
            ref int deduped, ref int cancelled)
        {
            // Exact duplicate -> drop.
            if (acc.Any(x => x.Op.Text == op.Text)) { deduped++; return; }

            // Create/drop cancellation: if the opposite already exists for the same object,
            // the later op wins and the earlier opposite is removed — both net to nothing,
            // so neither is emitted.
            var fam = FamilyOf(op.Type);
            if (fam != null && (CreateTypes.Contains(op.Type) || DropTypes.Contains(op.Type)))
            {
                bool isDrop = DropTypes.Contains(op.Type);
                int idx = acc.FindIndex(x =>
                    FamilyOf(x.Op.Type) == fam && x.Op.Key == op.Key && DropTypes.Contains(x.Op.Type) != isDrop);
                if (idx >= 0)
                {
                    acc.RemoveAt(idx);
                    cancelled++;
                    return; // current op is the inverse of the removed one -> net nothing
                }
            }
            acc.Add((source, op));
        }

        private static string RenderBody(List<(string Source, MigrationOp Op)> ops)
        {
            var sb = new StringBuilder();
            string? current = null;
            foreach (var (source, op) in ops)
            {
                if (source != current)
                {
                    sb.AppendLine($"            /* These operations come from {source} */");
                    current = source;
                }
                foreach (var line in op.Text.Replace(Environment.NewLine, "\n").Split('\n'))
                {
                    // Re-indent each operation line to a clean 12-space base.
                    var trimmed = line.Trim();
                    sb.AppendLine(string.IsNullOrEmpty(trimmed) ? "" : "            " + trimmed);
                }
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        // ---- Method body extraction ---------------------------------------------

        private static string ExtractMethodBody(string migrationContent, string methodName)
        {
            var pattern = methodName == "Up"
                ? @"protected\s+override\s+void\s+Up\s*\(\s*MigrationBuilder\s+migrationBuilder\s*\)\s*{"
                : @"protected\s+override\s+void\s+Down\s*\(\s*MigrationBuilder\s+migrationBuilder\s*\)\s*{";
            var match = Regex.Match(migrationContent, pattern);
            if (!match.Success) return string.Empty;

            int braceStart = match.Index + match.Length - 1;
            int depth = 1, pos = braceStart + 1;
            while (pos < migrationContent.Length && depth > 0)
            {
                if (migrationContent[pos] == '{') depth++;
                else if (migrationContent[pos] == '}') depth--;
                pos++;
            }
            if (depth != 0) return string.Empty;
            return migrationContent.Substring(braceStart + 1, pos - braceStart - 2).Trim();
        }

        // ---- Writing the merged migration + designer ----------------------------

        private async Task WriteMergedMigrationAsync(
            MigrationInfo last, string upBody, string downBody,
            string migrationNamespace, IReadOnlyList<string> usingDirectives)
        {
            Console.WriteLine($"📝 Rewriting last migration: {last.Id}");

            var usings = new[] { "using Microsoft.EntityFrameworkCore.Migrations;" }
                .Concat(usingDirectives)
                .Distinct(StringComparer.Ordinal);
            var usingBlock = string.Join(Environment.NewLine, usings);

            var content = $@"{usingBlock}

#nullable disable

namespace {migrationNamespace}
{{
    /// <inheritdoc />
    public partial class {last.Name} : Migration
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
        }}
    }}
}}";

            await File.WriteAllTextAsync(last.FilePath, content);
            Console.WriteLine($"✅ Updated: {Path.GetFileName(last.FilePath)}");
        }

        private async Task WriteDesignerAsync(
            MigrationInfo last, string modelSnapshot, string efCoreVersion, string migrationNamespace)
        {
            if (last.DesignerPath == null)
            {
                last = last with { DesignerPath = Path.Combine(_migrationsFolder, last.Id + ".Designer.cs") };
            }
            var designerContent = GenerateDesignerContent(last.Name, last.Id, modelSnapshot, efCoreVersion, migrationNamespace);
            await File.WriteAllTextAsync(last.DesignerPath, designerContent);
            Console.WriteLine($"✅ Updated: {Path.GetFileName(last.DesignerPath)}");
        }

        private string GenerateDesignerContent(
            string className, string migrationId, string modelSnapshot,
            string efCoreVersion, string migrationNamespace)
        {
            if (!string.IsNullOrWhiteSpace(modelSnapshot))
            {
                try
                {
                    var buildModelMatch = Regex.Match(modelSnapshot,
                        @"protected\s+override\s+void\s+BuildModel\s*\(\s*ModelBuilder\s+modelBuilder\s*\)\s*{");
                    if (buildModelMatch.Success)
                    {
                        var buildModelContent = ExtractBracedBody(modelSnapshot, buildModelMatch);
                        buildModelContent = Regex.Replace(
                            buildModelContent,
                            @"\.HasAnnotation\(""ProductVersion"",\s*""[^""]+""\)",
                            $@".HasAnnotation(""ProductVersion"", ""{efCoreVersion}"")");
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
    [Migration(""{migrationId}"")]
    partial class {className}
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

            return $@"// <auto-generated />
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace {migrationNamespace}
{{
    [DbContext(typeof({_contextName}))]
    [Migration(""{migrationId}"")]
    partial class {className}
    {{
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {{
            modelBuilder
                .HasAnnotation(""ProductVersion"", ""{efCoreVersion}"")
                .HasAnnotation(""Relational:MaxIdentifierLength"", 128);

            SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);
        }}
    }}
}}";
        }

        private static string ExtractBracedBody(string content, System.Text.RegularExpressions.Match signatureMatch)
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

        // ---- History cleanup script ---------------------------------------------

        private void GenerateHistoryCleanupScript(
            MigrationInfo last, List<MigrationInfo> removed, string efCoreVersion)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-- ===============================================");
            sb.AppendLine("-- EF Core Migration Merge - History Cleanup Script");
            sb.AppendLine($"-- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"-- Kept migration (already applied): {last.Id}");
            sb.AppendLine("-- Author: AmirTahan80");
            sb.AppendLine("-- ===============================================");
            sb.AppendLine("--");
            sb.AppendLine("-- This script only removes the migration ids that were folded into");
            sb.AppendLine("-- the last migration. The schema is NOT changed.");
            sb.AppendLine("--");
            sb.AppendLine("-- ⚠️  BACKUP your database before running this script.");
            sb.AppendLine("-- ===============================================");
            sb.AppendLine();
            sb.AppendLine("PRINT 'Starting EF Core migration history cleanup...';");
            sb.AppendLine();
            sb.AppendLine("-- Step 1: Review current history");
            sb.AppendLine("SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId;");
            sb.AppendLine();

            if (removed.Count > 0)
            {
                sb.AppendLine("-- Step 2: Remove the folded-away migration ids (the last one is kept).");
                sb.AppendLine("-- Uncomment after backup:");
                sb.AppendLine("-- DELETE FROM __EFMigrationsHistory");
                sb.AppendLine("-- WHERE MigrationId IN (");
                for (int i = 0; i < removed.Count; i++)
                {
                    var comma = i < removed.Count - 1 ? "," : "";
                    sb.AppendLine($"--   '{removed[i].Id}'{comma}");
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

            var scriptFile = Path.Combine(_migrationsFolder, "UpdateExistingDatabases.sql");
            File.WriteAllText(scriptFile, sb.ToString());
            Console.WriteLine($"📄 Generated history cleanup script: {Path.GetFileName(scriptFile)}");
        }
    }
}
