using System;
using System.Linq;
using EfMigrationSquasher.Core;
using Xunit;

namespace DotnetEfMigrationsSquashTool.Tests;

public class MigrationParserTests
{
    [Fact]
    public void ParseMigration_ExtractsUpAndDown_Correctly()
    {
        var code = @"
using Microsoft.EntityFrameworkCore.Migrations;
using System;

namespace MyApp.Migrations
{
    public partial class InitialCreate : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: ""Users"",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: ""Users"");
        }
    }
}";

        var result = MigrationParser.ParseMigration(code, "20260101120000_InitialCreate.cs");

        Assert.Equal("20260101120000_InitialCreate", result.MigrationId);
        Assert.Equal("InitialCreate", result.ClassName);
        Assert.NotNull(result.UpBody);
        Assert.Contains("migrationBuilder.CreateTable", result.UpBody);
        Assert.False(result.UpHasReturnStatement);
        Assert.NotNull(result.DownBody);
        Assert.Contains("migrationBuilder.DropTable", result.DownBody);
        Assert.Contains("using Microsoft.EntityFrameworkCore.Migrations;", result.Usings);
    }

    [Fact]
    public void ParseMigration_WithBracesInStringsAndComments_DoesNotTruncate()
    {
        // This was a fatal bug in the regex character-counter implementation
        var code = """
using Microsoft.EntityFrameworkCore.Migrations;

namespace MyApp.Migrations
{
    public partial class CustomMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Comment containing { curly braces }
            migrationBuilder.Sql("INSERT INTO Logs (Payload) VALUES ('{ \"key\": \"value\" }')");
            migrationBuilder.Sql("SELECT '{test}'");
            var query = "SELECT '{1 + 1}'";
            migrationBuilder.Sql(query);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM Logs WHERE Payload LIKE '%{ \"key\" }%'");
        }
    }
}
""";

        var result = MigrationParser.ParseMigration(code, "20260101130000_CustomMigration.cs");

        Assert.NotNull(result.UpBody);
        Assert.Contains("Comment containing { curly braces }", result.UpBody);
        Assert.Contains("migrationBuilder.Sql(query);", result.UpBody);
        Assert.NotNull(result.DownBody);
        Assert.Contains("DELETE FROM Logs", result.DownBody);
    }

    [Fact]
    public void ParseMigration_DetectsReturnStatement()
    {
        var code = @"
using Microsoft.EntityFrameworkCore.Migrations;

namespace MyApp.Migrations
{
    public partial class ConditionalMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.IsSqlServer())
            {
                return;
            }

            migrationBuilder.Sql(""DO SOMETHING"");
        }
    }
}";

        var result = MigrationParser.ParseMigration(code, "20260101140000_ConditionalMigration.cs");

        Assert.True(result.UpHasReturnStatement);
    }

    [Fact]
    public void ParseMigration_PreservesExtraClassMembers()
    {
        var code = @"
using Microsoft.EntityFrameworkCore.Migrations;

namespace MyApp.Migrations
{
    public partial class HelperMigration : Migration
    {
        private const string TableName = ""Audits"";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            SeedAudit(migrationBuilder);
        }

        private void SeedAudit(MigrationBuilder mb)
        {
            mb.Sql($""INSERT INTO {TableName} VALUES (1)"");
        }
    }
}";

        var result = MigrationParser.ParseMigration(code, "20260101150000_HelperMigration.cs");

        Assert.Contains(result.ExtraMembers, m => m.Contains("TableName"));
        Assert.Contains(result.ExtraMembers, m => m.Contains("SeedAudit"));
    }

    [Fact]
    public void ParseModelSnapshot_ExtractsContextAndBuildModel()
    {
        var code = @"
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MyApp.Models;

namespace MyApp.Migrations
{
    [DbContext(typeof(AppDbContext))]
    partial class AppDbContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
            modelBuilder
                .HasAnnotation(""ProductVersion"", ""10.0.3"")
                .HasAnnotation(""Relational:MaxIdentifierLength"", 128);

            modelBuilder.Entity(""MyApp.Models.User"", b =>
            {
                b.Property<int>(""Id"").ValueGeneratedOnAdd();
                b.HasKey(""Id"");
                b.ToTable(""Users"");
            });
        }
    }
}";

        var result = MigrationParser.ParseModelSnapshot(code);

        Assert.Equal("AppDbContext", result.DbContextName);
        Assert.Equal("MyApp.Migrations", result.Namespace);
        Assert.Equal("10.0.3", result.EfCoreVersion);
        Assert.NotNull(result.BuildModelBody);
        Assert.Contains("modelBuilder.Entity", result.BuildModelBody);
        Assert.Contains("using MyApp.Models;", result.Usings);
    }

    [Fact]
    public void SynthesizeDownOperations_FromUp_GeneratesSafeDropStatements()
    {
        var code = @"
using Microsoft.EntityFrameworkCore.Migrations;

namespace MyApp.Migrations
{
    public partial class CreateSchema : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: ""Departments"",
                columns: table => new { });

            migrationBuilder.CreateTable(
                name: ""Employees"",
                columns: table => new { });

            migrationBuilder.AddColumn<string>(
                name: ""Email"",
                table: ""Employees"",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: ""IX_Employees_Email"",
                table: ""Employees"",
                column: ""Email"");
        }
    }
}";

        var result = MigrationParser.ParseMigration(code, "20260101160000_CreateSchema.cs");

        Assert.Equal(4, result.SynthesizedDownOperations.Count);

        var dropIndex = result.SynthesizedDownOperations.FirstOrDefault(o => o.Code.Contains("DropIndex"));
        Assert.NotNull(dropIndex);
        Assert.Equal("migrationBuilder.DropIndex(name: \"IX_Employees_Email\", table: \"Employees\");", dropIndex.Code);

        var dropColumn = result.SynthesizedDownOperations.FirstOrDefault(o => o.Code.Contains("DropColumn"));
        Assert.NotNull(dropColumn);
        Assert.Equal("migrationBuilder.DropColumn(name: \"Email\", table: \"Employees\");", dropColumn.Code);

        var dropTables = result.SynthesizedDownOperations.Where(o => o.IsDropTable).ToList();
        Assert.Equal(2, dropTables.Count);
        Assert.Contains(dropTables, t => t.Code == "migrationBuilder.DropTable(name: \"Departments\");");
        Assert.Contains(dropTables, t => t.Code == "migrationBuilder.DropTable(name: \"Employees\");");
    }
}
