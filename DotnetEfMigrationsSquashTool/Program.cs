using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

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

            rootCommand.Options.Add(projectOption);
            rootCommand.Options.Add(contextOption);
            rootCommand.Options.Add(nameOption);
            rootCommand.Options.Add(dryRunOption);

            rootCommand.SetAction(async (parseResult) =>
            {
                var project = parseResult.GetValue(projectOption);
                var context = parseResult.GetValue(contextOption);
                var name = parseResult.GetValue(nameOption);
                var dryRun = parseResult.GetValue(dryRunOption);

                await SquashMigrationsAsync(project!, context!, name!, dryRun);
            });

            ParseResult parseResult = rootCommand.Parse(args);
            return await parseResult.InvokeAsync();
        }

        static async Task SquashMigrationsAsync(string projectPath, string contextName, string migrationName, bool dryRun)
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
                    return;
                }

                if (!projectPath.EndsWith(".csproj"))
                {
                    Console.WriteLine($"❌ Invalid project file. Must be a .csproj file: {projectPath}");
                    Console.WriteLine($"   You provided: {projectPath}");
                    Console.WriteLine($"   Example: --project \"./MyApp/MyApp.csproj\"");
                    return;
                }

                Console.WriteLine("✅ Project file found!");

                var migrationSquasher = new MigrationSquasher(projectPath, contextName);

                if (dryRun)
                {
                    await migrationSquasher.PreviewSquashAsync(migrationName);
                }
                else
                {
                    await migrationSquasher.SquashMigrationsAsync(migrationName);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
                Console.WriteLine($"🔍 Stack trace: {ex.StackTrace}");
            }
        }
    }
}