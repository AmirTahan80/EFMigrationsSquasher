using System.CommandLine;

namespace EfMigrationSquasher
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            var rootCommand = new RootCommand("EF Core Migration Squasher");

            // Simpler option definitions
            var projectOption = new Option<string>("--project") { Required = true };
            var contextOption = new Option<string>("--context") { Required = true };
            var migrationOption = new Option<string?>("--migration-root") { Required = false };
            var nameOption = new Option<string?>("--name")
            {
                DefaultValueFactory = (s) => null
            };
            var dryRunOption = new Option<bool>("--dry-run")
            {
                DefaultValueFactory = (s) => false
            };
            var optimizeOption = new Option<bool>("--optimize")
            {
                DefaultValueFactory = (s) => false
            };
            var updateDbOption = new Option<bool>("--update-database")
            {
                DefaultValueFactory = (s) => false
            };

            // Set descriptions separately
            projectOption.Description = "Path to the project file containing DbContext";
            contextOption.Description = "DbContext class name";
            nameOption.Description = "Name for the consolidated migration. If omitted, merges into the LAST existing migration (preserving its ID so existing databases require zero updates).";
            dryRunOption.Description = "Show what would be done without making changes";
            optimizeOption.Description = "Safely prune redundant operations (e.g. tables and columns created and subsequently dropped without raw SQL dependencies)";
            updateDbOption.Description = "Run 'dotnet ef database update' before merging migrations";
            migrationOption.Description = "Directory containing the Migrations folder or project (defaults to project directory)";

            rootCommand.Options.Add(projectOption);
            rootCommand.Options.Add(contextOption);
            rootCommand.Options.Add(nameOption);
            rootCommand.Options.Add(dryRunOption);
            rootCommand.Options.Add(optimizeOption);
            rootCommand.Options.Add(updateDbOption);
            rootCommand.Options.Add(migrationOption);

            rootCommand.SetAction(async (parseResult) =>
            {
                var project = parseResult.GetValue(projectOption);
                var context = parseResult.GetValue(contextOption);
                var name = parseResult.GetValue(nameOption);
                var dryRun = parseResult.GetValue(dryRunOption);
                var optimize = parseResult.GetValue(optimizeOption);
                var updateDb = parseResult.GetValue(updateDbOption);
                var migration = parseResult.GetValue(migrationOption);

                return await SquashHelper.SquashMigrationsAsync(project!, context!, migration, name, dryRun, optimize, updateDb);
            });

            ParseResult parseResult = rootCommand.Parse(args);
            return await parseResult.InvokeAsync();
        }
    }
}
