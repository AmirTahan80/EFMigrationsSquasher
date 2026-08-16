using System.Text.RegularExpressions;

namespace EfMigrationSquasher
{
    public static class SquashHelper
    {
        public static async Task<int> SquashMigrationsAsync(
            string projectPath, string contextName, string migration,
            string migrationName, bool dryRun, bool updateDatabase)
        {
            try
            {
                Console.WriteLine("🚀 EF Core Migration Squasher");
                Console.WriteLine("============================");
                Console.WriteLine($"🔍 Analyzing project: {projectPath}");
                Console.WriteLine($"📊 DbContext: {contextName}");
                Console.WriteLine();

                if (!File.Exists(projectPath))
                {
                    Console.WriteLine($"❌ Project file not found: {projectPath}");
                    return 1;
                }

                if (!Directory.Exists(migration))
                {
                    Console.WriteLine($"❌ Migration root directory not found: {migration}");
                    return 1;
                }

                var migrationsFolder = Path.Combine(migration, "Migrations");
                if (!Directory.Exists(migrationsFolder))
                {
                    Console.WriteLine($"❌ Migrations directory not found: {migrationsFolder}");
                    return 1;
                }

                if (!projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"❌ Invalid project file. Must be a .csproj file: {projectPath}");
                    Console.WriteLine($"   You provided: {projectPath}");
                    Console.WriteLine($"   Example: --project \"./MyApp/MyApp.csproj\"");
                    return 1;
                }

                Console.WriteLine("✅ Project file found!");

                var migrationSquasher = new MigrationSquasher(projectPath, contextName, migration);

                if (dryRun)
                {
                    await migrationSquasher.PreviewSquashAsync(updateDatabase);
                    return 0;
                }

                // Interactive confirmation for updating the database, unless explicitly set.
                if (!updateDatabase && Console.IsInputRedirected == false)
                {
                    updateDatabase = PromptYesNo(
                        "💾 Do you want to update the database BEFORE squashing? (dotnet ef database update) [y/N] ");
                }

                return await migrationSquasher.SquashMigrationsAsync(migrationName, updateDatabase);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
                Console.WriteLine($"🔍 Stack trace: {ex.StackTrace}");
                return 1;
            }
        }

        private static bool PromptYesNo(string message)
        {
            Console.Write(message);
            var key = Console.ReadKey(intercept: true);
            Console.WriteLine(key.KeyChar);
            // Default is No: only an explicit 'y' (or 'Y') confirms.
            return key.KeyChar == 'y' || key.KeyChar == 'Y';
        }
    }
}
