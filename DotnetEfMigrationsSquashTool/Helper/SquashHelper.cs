using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace EfMigrationSquasher;

public static class SquashHelper
{
    public static async Task<int> SquashMigrationsAsync(
        string projectPath,
        string contextName,
        string? migrationRoot,
        string? migrationName,
        bool dryRun,
        bool optimize = false,
        bool updateDatabase = false)
    {
        try
        {
            Console.WriteLine("🚀 EF Core Migration Squasher");
            Console.WriteLine("============================");
            Console.WriteLine($"🔍 Analyzing project: {projectPath}");
            Console.WriteLine($"📊 DbContext: {contextName}");
            Console.WriteLine();

            // Validate project file exists AND is a .csproj file
            if (!File.Exists(projectPath))
            {
                Console.WriteLine($"❌ Project file not found: {projectPath}");
                return 1;
            }

            if (!projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"❌ Invalid project file. Must be a .csproj file: {projectPath}");
                Console.WriteLine($"   You provided: {projectPath}");
                Console.WriteLine($"   Example: --project \"./MyApp/MyApp.csproj\"");
                return 1;
            }

            var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
            var resolvedMigrationRoot = string.IsNullOrWhiteSpace(migrationRoot)
                ? projectDir
                : migrationRoot;

            if (!Directory.Exists(resolvedMigrationRoot))
            {
                Console.WriteLine($"❌ Migration root directory not found: {resolvedMigrationRoot}");
                return 1;
            }

            // Resolve migrations directory intelligently
            string migrationsFolder;
            if (Path.GetFileName(resolvedMigrationRoot).Equals("Migrations", StringComparison.OrdinalIgnoreCase))
            {
                migrationsFolder = resolvedMigrationRoot;
            }
            else if (Directory.Exists(Path.Combine(resolvedMigrationRoot, "Migrations")))
            {
                migrationsFolder = Path.Combine(resolvedMigrationRoot, "Migrations");
            }
            else if (Directory.GetFiles(resolvedMigrationRoot, "*ModelSnapshot.cs").Length > 0)
            {
                migrationsFolder = resolvedMigrationRoot;
            }
            else
            {
                migrationsFolder = Path.Combine(resolvedMigrationRoot, "Migrations");
            }

            if (!Directory.Exists(migrationsFolder))
            {
                Console.WriteLine($"❌ Migrations directory not found: {migrationsFolder}");
                return 1;
            }

            if (!string.IsNullOrWhiteSpace(migrationName) && !Regex.IsMatch(migrationName, @"^[_\p{L}][\p{L}\p{Nd}_]*$"))
            {
                Console.WriteLine($"❌ Invalid migration name: {migrationName}");
                Console.WriteLine("   Use a valid C# identifier, for example: ConsolidatedMigration");
                return 1;
            }

            Console.WriteLine("✅ Project and migrations directory verified!");
            Console.WriteLine($"📁 Migrations folder: {migrationsFolder}");

            if (updateDatabase && !dryRun)
            {
                Console.WriteLine($"🔄 Running 'dotnet ef database update' for {contextName}...");
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"ef database update --project \"{projectPath}\" --context \"{contextName}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var output = await proc.StandardOutput.ReadToEndAsync();
                    var error = await proc.StandardError.ReadToEndAsync();
                    await proc.WaitForExitAsync();
                    if (proc.ExitCode != 0)
                    {
                        Console.WriteLine("❌ 'dotnet ef database update' failed:");
                        Console.WriteLine(string.IsNullOrWhiteSpace(error) ? output : error);
                        return 1;
                    }
                    Console.WriteLine("✅ Database updated successfully to latest migration!");
                }
            }

            var migrationSquasher = new MigrationSquasher(projectPath, contextName, migrationsFolder);

            if (dryRun)
            {
                await migrationSquasher.PreviewSquashAsync(migrationName, optimize);
            }
            else
            {
                await migrationSquasher.SquashMigrationsAsync(migrationName, optimize);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex.Message}");
            Console.WriteLine($"🔍 Stack trace: {ex.StackTrace}");
            return 1;
        }
    }
}
