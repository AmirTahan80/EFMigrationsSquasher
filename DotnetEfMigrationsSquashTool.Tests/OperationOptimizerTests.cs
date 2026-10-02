using System.Collections.Generic;
using EfMigrationSquasher.Core;
using Xunit;

namespace DotnetEfMigrationsSquashTool.Tests;

public class OperationOptimizerTests
{
    [Fact]
    public void Optimize_PrunesRedundantTable_WhenNoSqlDependenciesExist()
    {
        var m1 = new ParsedMigration
        {
            FileName = "20260101_Init.cs",
            MigrationId = "20260101_Init",
            ClassName = "Init",
            UpBody = @"
migrationBuilder.CreateTable(
    name: ""TempLogs"",
    columns: table => new { Id = table.Column<int>() });

migrationBuilder.CreateTable(
    name: ""Users"",
    columns: table => new { Id = table.Column<int>() });"
        };

        var m2 = new ParsedMigration
        {
            FileName = "20260102_DropLogs.cs",
            MigrationId = "20260102_DropLogs",
            ClassName = "DropLogs",
            UpBody = @"migrationBuilder.DropTable(name: ""TempLogs"");"
        };

        var migrations = new List<ParsedMigration> { m1, m2 };

        var result = OperationOptimizer.Optimize(migrations);

        Assert.Equal(2, result.PrunedOperationsCount);
        Assert.Single(result.PrunedMessages);
        Assert.Contains("TempLogs", result.PrunedMessages[0]);

        // In m1, TempLogs must be gone, but Users must remain!
        Assert.NotNull(m1.UpBody);
        Assert.DoesNotContain("TempLogs", m1.UpBody);
        Assert.Contains("Users", m1.UpBody);

        // In m2, DropTable was the only statement, so UpBody must be null/empty
        Assert.Null(m2.UpBody);
    }

    [Fact]
    public void Optimize_RetainsDroppedTable_WhenReferencedInRawSql()
    {
        var m1 = new ParsedMigration
        {
            FileName = "20260101_Init.cs",
            MigrationId = "20260101_Init",
            ClassName = "Init",
            UpBody = @"migrationBuilder.CreateTable(name: ""UserBackup"", columns: table => new { });"
        };

        var m2 = new ParsedMigration
        {
            FileName = "20260102_DataMigration.cs",
            MigrationId = "20260102_DataMigration",
            ClassName = "DataMigration",
            UpBody = @"migrationBuilder.Sql(""INSERT INTO UserBackup SELECT * FROM Users"");"
        };

        var m3 = new ParsedMigration
        {
            FileName = "20260103_Cleanup.cs",
            MigrationId = "20260103_Cleanup",
            ClassName = "Cleanup",
            UpBody = @"migrationBuilder.DropTable(name: ""UserBackup"");"
        };

        var migrations = new List<ParsedMigration> { m1, m2, m3 };

        var result = OperationOptimizer.Optimize(migrations);

        // Should NOT be pruned due to raw SQL safety check
        Assert.Equal(0, result.PrunedOperationsCount);
        Assert.Single(result.RetainedMessages);
        Assert.Contains("UserBackup", result.RetainedMessages[0]);
        Assert.Contains("referenced in raw SQL", result.RetainedMessages[0]);

        Assert.NotNull(m1.UpBody);
        Assert.Contains("UserBackup", m1.UpBody);
        Assert.NotNull(m3.UpBody);
        Assert.Contains("DropTable", m3.UpBody);
    }

    [Fact]
    public void Optimize_PrunesRedundantColumn_OnSurvivingTable()
    {
        var m1 = new ParsedMigration
        {
            FileName = "20260101_Init.cs",
            MigrationId = "20260101_Init",
            ClassName = "Init",
            UpBody = @"migrationBuilder.CreateTable(name: ""Users"", columns: table => new { });"
        };

        var m2 = new ParsedMigration
        {
            FileName = "20260102_AddColumn.cs",
            MigrationId = "20260102_AddColumn",
            ClassName = "AddColumn",
            UpBody = @"migrationBuilder.AddColumn<string>(name: ""LegacyToken"", table: ""Users"", nullable: true);"
        };

        var m3 = new ParsedMigration
        {
            FileName = "20260103_DropColumn.cs",
            MigrationId = "20260103_DropColumn",
            ClassName = "DropColumn",
            UpBody = @"migrationBuilder.DropColumn(name: ""LegacyToken"", table: ""Users"");"
        };

        var migrations = new List<ParsedMigration> { m1, m2, m3 };

        var result = OperationOptimizer.Optimize(migrations);

        Assert.Equal(2, result.PrunedOperationsCount);
        Assert.Single(result.PrunedMessages);
        Assert.Contains("Users.LegacyToken", result.PrunedMessages[0]);

        // Both m2 and m3 only contained LegacyToken operations, so their bodies are pruned
        Assert.Null(m2.UpBody);
        Assert.Null(m3.UpBody);

        // Users table in m1 is intact
        Assert.NotNull(m1.UpBody);
        Assert.Contains("Users", m1.UpBody);
    }

    [Fact]
    public void Optimize_RetainsColumn_WhenReferencedInRawSql()
    {
        var m1 = new ParsedMigration
        {
            FileName = "20260101_Init.cs",
            MigrationId = "20260101_Init",
            ClassName = "Init",
            UpBody = @"migrationBuilder.AddColumn<int>(name: ""TempScore"", table: ""Users"");"
        };

        var m2 = new ParsedMigration
        {
            FileName = "20260102_Sql.cs",
            MigrationId = "20260102_Sql",
            ClassName = "Sql",
            UpBody = @"migrationBuilder.Sql(""UPDATE Users SET FinalScore = TempScore * 2"");"
        };

        var m3 = new ParsedMigration
        {
            FileName = "20260103_Drop.cs",
            MigrationId = "20260103_Drop",
            ClassName = "Drop",
            UpBody = @"migrationBuilder.DropColumn(name: ""TempScore"", table: ""Users"");"
        };

        var migrations = new List<ParsedMigration> { m1, m2, m3 };

        var result = OperationOptimizer.Optimize(migrations);

        Assert.Equal(0, result.PrunedOperationsCount);
        Assert.Single(result.RetainedMessages);
        Assert.Contains("TempScore", result.RetainedMessages[0]);
    }

    [Fact]
    public void Optimize_PrunesRedundantIndex()
    {
        var m1 = new ParsedMigration
        {
            FileName = "20260101_AddIdx.cs",
            MigrationId = "20260101_AddIdx",
            ClassName = "AddIdx",
            UpBody = @"migrationBuilder.CreateIndex(name: ""IX_Temp"", table: ""Users"", column: ""Email"");"
        };

        var m2 = new ParsedMigration
        {
            FileName = "20260102_DropIdx.cs",
            MigrationId = "20260102_DropIdx",
            ClassName = "DropIdx",
            UpBody = @"migrationBuilder.DropIndex(name: ""IX_Temp"", table: ""Users"");"
        };

        var migrations = new List<ParsedMigration> { m1, m2 };

        var result = OperationOptimizer.Optimize(migrations);

        Assert.Equal(2, result.PrunedOperationsCount);
        Assert.Single(result.PrunedMessages);
        Assert.Contains("IX_Temp", result.PrunedMessages[0]);
        Assert.Null(m1.UpBody);
        Assert.Null(m2.UpBody);
    }

    [Fact]
    public void Optimize_RetainsDroppedTable_WhenReferencedBySurvivingTableForeignKey()
    {
        var m1 = new ParsedMigration
        {
            FileName = "20260101_Init.cs",
            MigrationId = "20260101_Init",
            ClassName = "Init",
            UpBody = @"
migrationBuilder.CreateTable(
    name: ""Categories"",
    columns: table => new { Id = table.Column<int>() });

migrationBuilder.CreateTable(
    name: ""Products"",
    columns: table => new { Id = table.Column<int>(), CategoryId = table.Column<int>() },
    constraints: table =>
    {
        table.ForeignKey(
            name: ""FK_Products_Categories"",
            column: x => x.CategoryId,
            principalTable: ""Categories"",
            principalColumn: ""Id"");
    });"
        };

        var m2 = new ParsedMigration
        {
            FileName = "20260102_DropCat.cs",
            MigrationId = "20260102_DropCat",
            ClassName = "DropCat",
            UpBody = @"migrationBuilder.DropTable(name: ""Categories"");"
        };

        var migrations = new List<ParsedMigration> { m1, m2 };

        var result = OperationOptimizer.Optimize(migrations);

        // Categories must NOT be pruned because surviving Products table has a FK referencing it
        Assert.Equal(0, result.PrunedOperationsCount);
        Assert.Single(result.RetainedMessages);
        Assert.Contains("Categories", result.RetainedMessages[0]);
        Assert.Contains("referenced by surviving table 'Products'", result.RetainedMessages[0]);
    }
}
