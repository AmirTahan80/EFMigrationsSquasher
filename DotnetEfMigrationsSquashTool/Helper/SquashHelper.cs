public static class SquashHelper
{
    public static async Task<int> SquashMigrationsAsync(string projectPath, string contextName, string migration, string migrationName, bool dryRun)
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

            if(!File.Exists(migration)))
            {
                Console.WriteLine($"❌ Migration files not found: {migration}");
                return 1;
            }

            if (!projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"❌ Invalid project file. Must be a .csproj file: {projectPath}");
                Console.WriteLine($"   You provided: {projectPath}");
                Console.WriteLine($"   Example: --project \"./MyApp/MyApp.csproj\"");
                return 1;
            }

            if (!Regex.IsMatch(migrationName, @"^[_\p{L}][\p{L}\p{Nd}_]*$"))
            {
                Console.WriteLine($"❌ Invalid migration name: {migrationName}");
                Console.WriteLine("   Use a valid C# identifier, for example: ConsolidatedMigration");
                return 1;
            }

            Console.WriteLine("✅ Project file found!");

            var migrationSquasher = new MigrationSquasher(projectPath, contextName, migration);

            if (dryRun)
            {
                await migrationSquasher.PreviewSquashAsync(migrationName);
            }
            else
            {
                await migrationSquasher.SquashMigrationsAsync(migrationName);
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