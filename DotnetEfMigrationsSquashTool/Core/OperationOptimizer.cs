using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfMigrationSquasher.Core;

public class OptimizationResult
{
    public List<string> PrunedMessages { get; } = new();
    public List<string> RetainedMessages { get; } = new();
    public int PrunedOperationsCount { get; set; }
}

public class MigrationOperation
{
    public required StatementSyntax Statement { get; init; }
    public required string OperationType { get; init; } // "CreateTable", "DropTable", "AddColumn", "DropColumn", "CreateIndex", "DropIndex", "Sql", "Other"
    public string? TableName { get; init; }
    public string? ColumnName { get; init; }
    public string? IndexName { get; init; }
    public string? PrincipalTable { get; init; }
    public string? SqlText { get; init; }
    public required string MigrationId { get; init; }
    public required int MigrationIndex { get; init; }
    public bool IsPruned { get; set; }
}

public static class OperationOptimizer
{
    public static OptimizationResult Optimize(List<ParsedMigration> migrations)
    {
        var result = new OptimizationResult();

        // 1. Parse all statements from all migrations
        var operationsByMigration = new List<(ParsedMigration Migration, List<MigrationOperation> Operations)>();
        var allRawSql = new List<(string Sql, string MigrationId)>();

        for (int i = 0; i < migrations.Count; i++)
        {
            var migration = migrations[i];
            var ops = ExtractOperations(migration.UpBody, migration.MigrationId, i);
            operationsByMigration.Add((migration, ops));

            foreach (var op in ops.Where(o => o.OperationType == "Sql" && !string.IsNullOrWhiteSpace(o.SqlText)))
            {
                allRawSql.Add((op.SqlText!, op.MigrationId));
            }
        }

        var allOps = operationsByMigration.SelectMany(m => m.Operations).ToList();

        // 2. Identify Redundant Tables (Created and subsequently Dropped)
        var createdTables = allOps
            .Where(o => o.OperationType == "CreateTable" && !string.IsNullOrWhiteSpace(o.TableName))
            .GroupBy(o => o.TableName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var droppedTables = allOps
            .Where(o => o.OperationType == "DropTable" && !string.IsNullOrWhiteSpace(o.TableName))
            .GroupBy(o => o.TableName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var prunedTableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (tableName, createOp) in createdTables)
        {
            if (droppedTables.TryGetValue(tableName, out var dropOp))
            {
                // Must be dropped after creation
                if (dropOp.MigrationIndex >= createOp.MigrationIndex)
                {
                    // Safety Check 1: Is this table referenced in any raw SQL?
                    var sqlReference = allRawSql.FirstOrDefault(s =>
                        Regex.IsMatch(s.Sql, $@"\b{Regex.Escape(tableName)}\b", RegexOptions.IgnoreCase));

                    if (sqlReference.Sql != null)
                    {
                        result.RetainedMessages.Add(
                            $"Table '{tableName}' was dropped in {dropOp.MigrationId}, but retained for safety (referenced in raw SQL in {sqlReference.MigrationId}).");
                        continue;
                    }

                    // Safety Check 2: Does any surviving table reference this table via Foreign Key?
                    var survivingFk = allOps.FirstOrDefault(o =>
                        o.OperationType == "CreateTable"
                        && !string.Equals(o.TableName, tableName, StringComparison.OrdinalIgnoreCase)
                        && !droppedTables.ContainsKey(o.TableName ?? "")
                        && string.Equals(o.PrincipalTable, tableName, StringComparison.OrdinalIgnoreCase));

                    if (survivingFk != null)
                    {
                        result.RetainedMessages.Add(
                            $"Table '{tableName}' was dropped in {dropOp.MigrationId}, but retained for safety (referenced by surviving table '{survivingFk.TableName}').");
                        continue;
                    }

                    // Safe to prune table!
                    prunedTableNames.Add(tableName);
                    createOp.IsPruned = true;
                    dropOp.IsPruned = true;

                    // Prune all other operations touching this table
                    foreach (var op in allOps.Where(o => string.Equals(o.TableName, tableName, StringComparison.OrdinalIgnoreCase)))
                    {
                        op.IsPruned = true;
                    }

                    result.PrunedMessages.Add(
                        $"Pruned redundant table '{tableName}' (Created in {createOp.MigrationId}, Dropped in {dropOp.MigrationId}).");
                }
            }
        }

        // 3. Identify Redundant Columns on Surviving Tables (Added and subsequently Dropped)
        var addedColumns = allOps
            .Where(o => o.OperationType == "AddColumn"
                        && !string.IsNullOrWhiteSpace(o.TableName)
                        && !string.IsNullOrWhiteSpace(o.ColumnName)
                        && !prunedTableNames.Contains(o.TableName!))
            .GroupBy(o => $"{o.TableName}.{o.ColumnName}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var droppedColumns = allOps
            .Where(o => o.OperationType == "DropColumn"
                        && !string.IsNullOrWhiteSpace(o.TableName)
                        && !string.IsNullOrWhiteSpace(o.ColumnName)
                        && !prunedTableNames.Contains(o.TableName!))
            .GroupBy(o => $"{o.TableName}.{o.ColumnName}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var (colKey, addOp) in addedColumns)
        {
            if (droppedColumns.TryGetValue(colKey, out var dropOp))
            {
                if (dropOp.MigrationIndex >= addOp.MigrationIndex)
                {
                    // Safety Check: Is the column referenced in raw SQL?
                    var sqlReference = allRawSql.FirstOrDefault(s =>
                        Regex.IsMatch(s.Sql, $@"\b{Regex.Escape(addOp.ColumnName!)}\b", RegexOptions.IgnoreCase));

                    if (sqlReference.Sql != null)
                    {
                        result.RetainedMessages.Add(
                            $"Column '{colKey}' was dropped in {dropOp.MigrationId}, but retained for safety (referenced in raw SQL in {sqlReference.MigrationId}).");
                        continue;
                    }

                    // Safe to prune column!
                    addOp.IsPruned = true;
                    dropOp.IsPruned = true;

                    result.PrunedMessages.Add(
                        $"Pruned redundant column '{colKey}' (Added in {addOp.MigrationId}, Dropped in {dropOp.MigrationId}).");
                }
            }
        }

        // 4. Identify Redundant Indexes (Created and subsequently Dropped)
        var createdIndexes = allOps
            .Where(o => o.OperationType == "CreateIndex"
                        && !string.IsNullOrWhiteSpace(o.IndexName)
                        && !prunedTableNames.Contains(o.TableName ?? ""))
            .GroupBy(o => o.IndexName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var droppedIndexes = allOps
            .Where(o => o.OperationType == "DropIndex"
                        && !string.IsNullOrWhiteSpace(o.IndexName)
                        && !prunedTableNames.Contains(o.TableName ?? ""))
            .GroupBy(o => o.IndexName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var (indexName, createIdxOp) in createdIndexes)
        {
            if (droppedIndexes.TryGetValue(indexName, out var dropIdxOp))
            {
                if (dropIdxOp.MigrationIndex >= createIdxOp.MigrationIndex)
                {
                    createIdxOp.IsPruned = true;
                    dropIdxOp.IsPruned = true;

                    result.PrunedMessages.Add(
                        $"Pruned redundant index '{indexName}' (Created in {createIdxOp.MigrationId}, Dropped in {dropIdxOp.MigrationId}).");
                }
            }
        }

        // 5. Rebuild UpBody and Down operations for each migration
        int totalPrunedCount = 0;
        foreach (var (migration, ops) in operationsByMigration)
        {
            var prunedInMigration = ops.Where(o => o.IsPruned).Select(o => o.Statement).ToHashSet();
            totalPrunedCount += prunedInMigration.Count;

            if (prunedInMigration.Count > 0 && !string.IsNullOrWhiteSpace(migration.UpBody))
            {
                migration.UpBody = RebuildBodyExcludingPruned(migration.UpBody, prunedInMigration);
            }

            // Also filter synthesized down operations
            migration.SynthesizedDownOperations.RemoveAll(op =>
            {
                foreach (var table in prunedTableNames)
                {
                    if (Regex.IsMatch(op.Code, $@"\b{Regex.Escape(table)}\b"))
                    {
                        return true;
                    }
                }
                return false;
            });
        }

        result.PrunedOperationsCount = totalPrunedCount;
        return result;
    }

    private static List<MigrationOperation> ExtractOperations(string? bodyCode, string migrationId, int migrationIndex)
    {
        var list = new List<MigrationOperation>();
        if (string.IsNullOrWhiteSpace(bodyCode))
            return list;

        var wrappedCode = $@"
class Wrapper {{
    void Method() {{
{bodyCode}
    }}
}}";

        var tree = CSharpSyntaxTree.ParseText(wrappedCode);
        var root = tree.GetCompilationUnitRoot();

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (method?.Body == null)
            return list;

        foreach (var statement in method.Body.Statements)
        {
            var op = ParseStatementOperation(statement, migrationId, migrationIndex);
            list.Add(op);
        }

        return list;
    }

    private static MigrationOperation ParseStatementOperation(StatementSyntax statement, string migrationId, int migrationIndex)
    {
        if (statement is ExpressionStatementSyntax exprStmt
            && exprStmt.Expression is InvocationExpressionSyntax invocation
            && invocation.Expression is MemberAccessExpressionSyntax memberAccess
            && memberAccess.Expression.ToString().Contains("migrationBuilder"))
        {
            var methodName = memberAccess.Name.Identifier.Text;
            var args = invocation.ArgumentList.Arguments;

            switch (methodName)
            {
                case "CreateTable":
                {
                    var tableName = GetStringArg(args, "name", 0);
                    var principalTable = ExtractPrincipalTableFromForeignKeys(invocation);
                    return new MigrationOperation
                    {
                        Statement = statement,
                        OperationType = "CreateTable",
                        TableName = tableName,
                        PrincipalTable = principalTable,
                        MigrationId = migrationId,
                        MigrationIndex = migrationIndex
                    };
                }

                case "DropTable":
                {
                    var tableName = GetStringArg(args, "name", 0);
                    return new MigrationOperation
                    {
                        Statement = statement,
                        OperationType = "DropTable",
                        TableName = tableName,
                        MigrationId = migrationId,
                        MigrationIndex = migrationIndex
                    };
                }

                case "AddColumn":
                {
                    var colName = GetStringArg(args, "name", 0);
                    var tableName = GetStringArg(args, "table", 1);
                    return new MigrationOperation
                    {
                        Statement = statement,
                        OperationType = "AddColumn",
                        TableName = tableName,
                        ColumnName = colName,
                        MigrationId = migrationId,
                        MigrationIndex = migrationIndex
                    };
                }

                case "DropColumn":
                {
                    var colName = GetStringArg(args, "name", 0);
                    var tableName = GetStringArg(args, "table", 1);
                    return new MigrationOperation
                    {
                        Statement = statement,
                        OperationType = "DropColumn",
                        TableName = tableName,
                        ColumnName = colName,
                        MigrationId = migrationId,
                        MigrationIndex = migrationIndex
                    };
                }

                case "CreateIndex":
                {
                    var indexName = GetStringArg(args, "name", 0);
                    var tableName = GetStringArg(args, "table", 1);
                    return new MigrationOperation
                    {
                        Statement = statement,
                        OperationType = "CreateIndex",
                        TableName = tableName,
                        IndexName = indexName,
                        MigrationId = migrationId,
                        MigrationIndex = migrationIndex
                    };
                }

                case "DropIndex":
                {
                    var indexName = GetStringArg(args, "name", 0);
                    var tableName = GetStringArg(args, "table", 1);
                    return new MigrationOperation
                    {
                        Statement = statement,
                        OperationType = "DropIndex",
                        TableName = tableName,
                        IndexName = indexName,
                        MigrationId = migrationId,
                        MigrationIndex = migrationIndex
                    };
                }

                case "Sql":
                {
                    var sql = GetStringArg(args, "sql", 0) ?? args.FirstOrDefault()?.ToFullString();
                    return new MigrationOperation
                    {
                        Statement = statement,
                        OperationType = "Sql",
                        SqlText = sql,
                        MigrationId = migrationId,
                        MigrationIndex = migrationIndex
                    };
                }
            }
        }

        return new MigrationOperation
        {
            Statement = statement,
            OperationType = "Other",
            MigrationId = migrationId,
            MigrationIndex = migrationIndex
        };
    }

    private static string? ExtractPrincipalTableFromForeignKeys(InvocationExpressionSyntax createTableInvocation)
    {
        // Search inside CreateTable for foreignKey lambdas: table => table.ForeignKey(..., principalTable: "TargetTable")
        var foreignKeyInvocations = createTableInvocation.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(inv => inv.Expression.ToString().Contains("ForeignKey"));

        foreach (var fk in foreignKeyInvocations)
        {
            var principalTable = GetStringArg(fk.ArgumentList.Arguments, "principalTable", -1);
            if (!string.IsNullOrWhiteSpace(principalTable))
            {
                return principalTable;
            }
        }

        return null;
    }

    private static string? RebuildBodyExcludingPruned(string bodyCode, HashSet<StatementSyntax> prunedStatements)
    {
        var wrappedCode = $@"
class Wrapper {{
    void Method() {{
{bodyCode}
    }}
}}";

        var tree = CSharpSyntaxTree.ParseText(wrappedCode);
        var root = tree.GetCompilationUnitRoot();

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (method?.Body == null)
            return bodyCode;

        var survivingStatements = new List<string>();

        // We match statements by their exact span or normalized string representation
        var prunedStrings = prunedStatements.Select(p => p.ToFullString().Trim()).ToHashSet();

        foreach (var stmt in method.Body.Statements)
        {
            var stmtText = stmt.ToFullString().Trim();
            if (!prunedStrings.Contains(stmtText))
            {
                survivingStatements.Add(stmt.ToFullString().TrimEnd());
            }
        }

        if (survivingStatements.Count == 0)
        {
            return null;
        }

        return string.Join(Environment.NewLine, survivingStatements);
    }

    private static string? GetStringArg(SeparatedSyntaxList<ArgumentSyntax> args, string name, int index)
    {
        var named = args.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == name);
        if (named != null)
        {
            return ExtractLiteralOrIdentifier(named.Expression);
        }

        if (index >= 0 && index < args.Count && args[index].NameColon == null)
        {
            return ExtractLiteralOrIdentifier(args[index].Expression);
        }

        return null;
    }

    private static string? ExtractLiteralOrIdentifier(ExpressionSyntax expr)
    {
        if (expr is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return literal.Token.ValueText;
        }

        return null;
    }
}
