using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    public void GetFilesToRemove_WhenMergingIntoLast_PreservesLastMigration()
    {
        File.WriteAllText(Path.Combine(_migrationsDir, "20260101100000_Init.cs"), "// migration 1");
        File.WriteAllText(Path.Combine(_migrationsDir, "20260101100000_Init.Designer.cs"), "// designer 1");
        File.WriteAllText(Path.Combine(_migrationsDir, "20260102120000_AddUsers.cs"), "// migration 2 (last)");
        File.WriteAllText(Path.Combine(_migrationsDir, "20260102120000_AddUsers.Designer.cs"), "// designer 2");
        File.WriteAllText(Path.Combine(_migrationsDir, "TestDbContextModelSnapshot.cs"), "// snapshot");
        File.WriteAllText(Path.Combine(_migrationsDir, "MigrationHelper.cs"), "// custom helper");

        var squasher = new MigrationSquasher(_projectFile, "TestDbContext", _migrationsDir);
        // null means merge into last migration
        var filesToRemove = squasher.GetFilesToRemove(null);

        // Only migration 1 and its designer should be removed
        Assert.Equal(2, filesToRemove.Count);
        Assert.Contains(filesToRemove, f => Path.GetFileName(f) == "20260101100000_Init.cs");
        Assert.Contains(filesToRemove, f => Path.GetFileName(f) == "20260101100000_Init.Designer.cs");

        // Migration 2 must NOT be in filesToRemove because it is kept
        Assert.DoesNotContain(filesToRemove, f => Path.GetFileName(f) == "20260102120000_AddUsers.cs");
        Assert.DoesNotContain(filesToRemove, f => Path.GetFileName(f) == "TestDbContextModelSnapshot.cs");
        Assert.DoesNotContain(filesToRemove, f => Path.GetFileName(f) == "MigrationHelper.cs");
    }

    [Fact]
    public void GetFilesToRemove_WhenCustomNameSpecified_RemovesAllOldMigrations()
    {
        File.WriteAllText(Path.Combine(_migrationsDir, "20260101100000_Init.cs"), "// migration 1");
        File.WriteAllText(Path.Combine(_migrationsDir, "20260101100000_Init.Designer.cs"), "// designer 1");
        File.WriteAllText(Path.Combine(_migrationsDir, "20260102120000_AddUsers.cs"), "// migration 2");
        File.WriteAllText(Path.Combine(_migrationsDir, "20260102120000_AddUsers.Designer.cs"), "// designer 2");

        var squasher = new MigrationSquasher(_projectFile, "TestDbContext", _migrationsDir);
        var filesToRemove = squasher.GetFilesToRemove("CustomConsolidated");

        Assert.Equal(4, filesToRemove.Count);
        Assert.Contains(filesToRemove, f => Path.GetFileName(f) == "20260101100000_Init.cs");
        Assert.Contains(filesToRemove, f => Path.GetFileName(f) == "20260102120000_AddUsers.cs");
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

    [Fact]
    public async Task SquashMigrationsAsync_WhenMergingIntoLast_KeepsLastIdAndGeneratesCleanupScript()
    {
        var m1 = @"
using Microsoft.EntityFrameworkCore.Migrations;
namespace MyApp.Migrations
{
    public partial class Init : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(name: ""Users"", columns: table => new { });
        }
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: ""Users"");
        }
    }
}";
        var m2 = @"
using Microsoft.EntityFrameworkCore.Migrations;
namespace MyApp.Migrations
{
    public partial class AddOrders : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(name: ""Orders"", columns: table => new { });
        }
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: ""Orders"");
        }
    }
}";
        var snapshot = @"
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
namespace MyApp.Migrations
{
    [DbContext(typeof(TestDbContext))]
    partial class TestDbContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder) { }
    }
}";

        File.WriteAllText(Path.Combine(_migrationsDir, "20260101100000_Init.cs"), m1);
        File.WriteAllText(Path.Combine(_migrationsDir, "20260101100000_Init.Designer.cs"), "// designer 1");
        File.WriteAllText(Path.Combine(_migrationsDir, "20260102120000_AddOrders.cs"), m2);
        File.WriteAllText(Path.Combine(_migrationsDir, "20260102120000_AddOrders.Designer.cs"), "// designer 2");
        File.WriteAllText(Path.Combine(_migrationsDir, "TestDbContextModelSnapshot.cs"), snapshot);

        var squasher = new MigrationSquasher(_projectFile, "TestDbContext", _migrationsDir);

        // Merge into last migration (name = null)
        await squasher.SquashMigrationsAsync(null, optimize: false);

        // Migration 1 should be deleted
        Assert.False(File.Exists(Path.Combine(_migrationsDir, "20260101100000_Init.cs")));
        Assert.False(File.Exists(Path.Combine(_migrationsDir, "20260101100000_Init.Designer.cs")));

        // Migration 2 (the last migration) MUST exist!
        Assert.True(File.Exists(Path.Combine(_migrationsDir, "20260102120000_AddOrders.cs")));
        Assert.True(File.Exists(Path.Combine(_migrationsDir, "20260102120000_AddOrders.Designer.cs")));

        // Contents of Migration 2 must now have both Users and Orders in Up()
        var mergedContent = File.ReadAllText(Path.Combine(_migrationsDir, "20260102120000_AddOrders.cs"));
        Assert.Contains("Users", mergedContent);
        Assert.Contains("Orders", mergedContent);

        // The cleanup script must be generated
        var sqlScript = Path.Combine(_migrationsDir, "UpdateExistingDatabases.sql");
        Assert.True(File.Exists(sqlScript));
        var sqlText = File.ReadAllText(sqlScript);
        Assert.Contains("Kept migration (already applied): 20260102120000_AddOrders", sqlText);
        Assert.Contains("20260101100000_Init", sqlText);
    }
}
