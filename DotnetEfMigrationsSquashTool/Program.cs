using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using System.Text.RegularExpressions;

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
            var migrationOption = new Option<string>("--migration-root") { Required = true };
            var nameOption = new Option<string>("--name")
            {
                DefaultValueFactory = (s) => "ConsolidatedMigration"
            };
            var dryRunOption = new Option<bool>("--dry-run")
            {
                DefaultValueFactory = (s) => false
            };

            // Set descriptions separately
            projectOption.Description = "Path to the project file containing DbContext";
            contextOption.Description = "DbContext class name";
            nameOption.Description = "Name for the new consolidated migration";
            dryRunOption.Description = "Show what would be done without making changes";
            migrationOption.Description = "Path to migration files";

            rootCommand.Options.Add(projectOption);
            rootCommand.Options.Add(contextOption);
            rootCommand.Options.Add(nameOption);
            rootCommand.Options.Add(dryRunOption);
            rootCommand.option.Add(migrationOption)

            rootCommand.SetAction(async (parseResult) =>
            {
                var project = parseResult.GetValue(projectOption);
                var context = parseResult.GetValue(contextOption);
                var name = parseResult.GetValue(nameOption);
                var dryRun = parseResult.GetValue(dryRunOption);
                var migration = parseResult.GetValue(migrationOption)

                return await SquashHelper.SquashMigrationsAsync(project!, context!, migration!, name!, dryRun);
            });

            ParseResult parseResult = rootCommand.Parse(args);
            return await parseResult.InvokeAsync();
        }
    }
}
