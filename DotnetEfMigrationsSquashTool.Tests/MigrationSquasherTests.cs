using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using EfMigrationSquasher.Core;
using Xunit;

namespace DotnetEfMigrationsSquashTool.Tests;

public class MigrationSquasherTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _migrationsDir;
    private readonly string _projectFile;

    public MigrationSquasherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "EfSquashTest_" + Guid.NewGuid().ToString("N"));
        _migrationsDir = Path.Combine(_tempDir, "Migrations");
        Directory.CreateDirectory(_migrationsDir);
        _projectFile = Path.Combine(_tempDir, "TestApp.csproj");
        File.WriteAllText(_projectFile, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in temp
        }
    }

    [Fact]
    public void GetExistingMigrationFiles_OnlyMatchesValidMigrationFiles()
    {
        // Valid migrations
        File.WriteAllText(Path.Combine(_migrationsDir, "20260101100000_Init.cs"), "// migration 1");
        File.WriteAllText(Path.Combine(_migrationsDir, "20260101100000_Init.Designer.cs"), "// designer 1");
        File.WriteAllText(Path.Combine(_migrationsDir, "20260102120000_AddUsers.cs"), "// migration 2");
        File.WriteAllText(Path.Combine(_migrationsDir, "20260102120000_AddUsers.Designer.cs"), "// designer 2");

        // Non-migration files
        File.WriteAllText(Path.Combine(_migrationsDir, "TestDbContextModelSnapshot.cs"), "// snapshot");
        File.WriteAllText(Path.Combine(_migrationsDir, "MigrationHelper.cs"), "// custom helper");
        File.WriteAllText(Path.Combine(_migrationsDir, "CustomEnum.cs"), "// custom enum");

        var squasher = new MigrationSquasher(_projectFile, "TestDbContext", _migrationsDir);
        var migrations = squasher.GetExistingMigrationFiles();

        Assert.Equal(2, migrations.Count);
        Assert.Contains(migrations, m => Path.GetFileName(m) == "20260101100000_Init.cs");
        Assert.Contains(migrations, m => Path.GetFileName(m) == "20260102120000_AddUsers.cs");
    }

    [Fact]
    public void GetFilesToRemove_NeverIncludesCustomClassesOrSnapshots()
    {
        File.WriteAllText(Path.Combine(_migrationsDir, "20260101100000_Init.cs"), "// migration 1");
        File.WriteAllText(Path.Combine(_migrationsDir, "20260101100000_Init.Designer.cs"), "// designer 1");
        File.WriteAllText(Path.Combine(_migrationsDir, "TestDbContextModelSnapshot.cs"), "// snapshot");
        File.WriteAllText(Path.Combine(_migrationsDir, "MigrationHelper.cs"), "// custom helper");

        var squasher = new MigrationSquasher(_projectFile, "TestDbContext", _migrationsDir);
        var filesToRemove = squasher.GetFilesToRemove();

        Assert.Equal(2, filesToRemove.Count);
        Assert.Contains(filesToRemove, f => Path.GetFileName(f) == "20260101100000_Init.cs");
        Assert.Contains(filesToRemove, f => Path.GetFileName(f) == "20260101100000_Init.Designer.cs");
        Assert.DoesNotContain(filesToRemove, f => Path.GetFileName(f) == "TestDbContextModelSnapshot.cs");
        Assert.DoesNotContain(filesToRemove, f => Path.GetFileName(f) == "MigrationHelper.cs");
    }

    [Fact]
    public void GenerateMigrationAndDesigner_WrapsReturnInLocalFunction()
    {
        var squasher = new MigrationSquasher(_projectFile, "TestDbContext", _migrationsDir);

        var parsedMigrations = new List<ParsedMigration>
        {
            new ParsedMigration
            {
                FileName = "20260101100000_Init.cs",
                MigrationId = "20260101100000_Init",
                ClassName = "Init",
                UpBody = "if (true) return;\nmigrationBuilder.CreateTable(name: \"T1\");",
                UpHasReturnStatement = true,
                DownBody = "migrationBuilder.DropTable(name: \"T1\");",
                DownHasReturnStatement = false,
                Usings = new List<string> { "using System;" }
            }
        };

        var parsedSnapshot = new ParsedModelSnapshot
        {
            DbContextName = "TestDbContext",
            Namespace = "MyApp.Migrations",
            EfCoreVersion = "10.0.3",
            BuildModelBody = "modelBuilder.HasAnnotation(\"ProductVersion\", \"10.0.3\");",
            Usings = new List<string> { "using Microsoft.EntityFrameworkCore;" }
        };

        var (migrationContent, designerContent) = squasher.GenerateMigrationAndDesigner(
            "ConsolidatedMigration",
            "20260103100000_ConsolidatedMigration",
            parsedMigrations,
            parsedSnapshot,
            "10.0.3",
            "MyApp.Migrations");

        // The early return should be safely isolated inside void Step_1()
        Assert.Contains("void Step_1()", migrationContent);
        Assert.Contains("Step_1();", migrationContent);
        Assert.Contains("migrationBuilder.CreateTable(name: \"T1\");", migrationContent);
        Assert.Contains("migrationBuilder.DropTable(name: \"T1\");", migrationContent);

        // Designer content should contain DbContext attribute and model builder content
        Assert.Contains("[DbContext(typeof(TestDbContext))]", designerContent);
        Assert.Contains("[Migration(\"20260103100000_ConsolidatedMigration\")]", designerContent);
        Assert.Contains("BuildTargetModel", designerContent);
    }
}
