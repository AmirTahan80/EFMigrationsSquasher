using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EfMigrationSquasher.Core;

public class ParsedMigration
{
    public required string FileName { get; init; }
    public required string MigrationId { get; init; }
    public required string ClassName { get; init; }
    public string? UpBody { get; init; }
    public bool UpHasReturnStatement { get; init; }
    public string? DownBody { get; init; }
    public bool DownHasReturnStatement { get; init; }
    public List<string> Usings { get; init; } = new();
    public List<string> ExtraMembers { get; init; } = new();
    public List<ReversedOperation> SynthesizedDownOperations { get; init; } = new();
}

public class ReversedOperation
{
    public required string Code { get; init; }
    public bool IsDropTable { get; init; }
}

public class ParsedModelSnapshot
{
    public string? SnapshotClassName { get; init; }
    public string? DbContextName { get; init; }
    public string? Namespace { get; init; }
    public string? EfCoreVersion { get; init; }
    public string? BuildModelBody { get; init; }
    public List<string> Usings { get; init; } = new();
}

public static class MigrationParser
{
    private const string DefaultEfCoreVersion = "10.0.3";

    public static ParsedMigration ParseMigration(string sourceCode, string fileName)
    {
        var migrationId = Path.GetFileNameWithoutExtension(fileName);
        var className = migrationId;
        var underscoreIndex = migrationId.IndexOf('_');
        if (underscoreIndex >= 0 && underscoreIndex + 1 < migrationId.Length)
        {
            className = migrationId[(underscoreIndex + 1)..];
        }

        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        // Extract all using directives across the file
        var usings = root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Select(u => u.ToFullString().Trim())
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Find the Migration class
        var classDeclaration = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.BaseList?.Types.Any(t => t.Type.ToString().Contains("Migration")) == true
                              || c.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == "Up" || m.Identifier.Text == "Down"))
            ?? root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();

        string? upBody = null;
        bool upHasReturn = false;
        string? downBody = null;
        bool downHasReturn = false;
        var extraMembers = new List<string>();
        var synthesizedDownOps = new List<ReversedOperation>();

        if (classDeclaration != null)
        {
            var upMethod = classDeclaration.Members
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == "Up" && m.Modifiers.Any(SyntaxKind.OverrideKeyword));

            if (upMethod != null)
            {
                (upBody, upHasReturn) = ExtractMethodBody(upMethod, sourceCode);
                if (upMethod.Body != null)
                {
                    synthesizedDownOps = SynthesizeDownOperations(upMethod.Body);
                }
            }

            var downMethod = classDeclaration.Members
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == "Down" && m.Modifiers.Any(SyntaxKind.OverrideKeyword));

            if (downMethod != null)
            {
                (downBody, downHasReturn) = ExtractMethodBody(downMethod, sourceCode);
            }

            // Extract any helper methods, properties, or fields declared in this migration class
            foreach (var member in classDeclaration.Members)
            {
                if (member != upMethod && member != downMethod)
                {
                    var memberCode = member.ToFullString().Trim();
                    if (!string.IsNullOrWhiteSpace(memberCode))
                    {
                        extraMembers.Add(memberCode);
                    }
                }
            }
        }

        return new ParsedMigration
        {
            FileName = fileName,
            MigrationId = migrationId,
            ClassName = className,
            UpBody = upBody,
            UpHasReturnStatement = upHasReturn,
            DownBody = downBody,
            DownHasReturnStatement = downHasReturn,
            Usings = usings,
            ExtraMembers = extraMembers,
            SynthesizedDownOperations = synthesizedDownOps
        };
    }

    public static ParsedModelSnapshot ParseModelSnapshot(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        // Extract namespace
        var ns = root.DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault()?.Name.ToString();

        // Extract using directives
        var usings = root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Select(u => u.ToFullString().Trim())
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Find ModelSnapshot class
        var classDeclaration = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.BaseList?.Types.Any(t => t.Type.ToString().Contains("ModelSnapshot")) == true
                              || c.Identifier.Text.EndsWith("ModelSnapshot", StringComparison.OrdinalIgnoreCase))
            ?? root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();

        string? snapshotClassName = classDeclaration?.Identifier.Text;
        string? dbContextName = null;

        // Try extracting DbContext from [DbContext(typeof(MyDbContext))]
        if (classDeclaration != null)
        {
            foreach (var attrList in classDeclaration.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    if (attr.Name.ToString().Contains("DbContext") && attr.ArgumentList != null)
                    {
                        var arg = attr.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                        if (arg is TypeOfExpressionSyntax typeOfExpr)
                        {
                            dbContextName = typeOfExpr.Type.ToString();
                        }
                    }
                }
            }
        }

        string? buildModelBody = null;
        string efCoreVersion = DefaultEfCoreVersion;

        if (classDeclaration != null)
        {
            var buildModelMethod = classDeclaration.Members
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text is "BuildModel" or "BuildTargetModel");

            if (buildModelMethod != null)
            {
                var (body, _) = ExtractMethodBody(buildModelMethod, sourceCode);
                buildModelBody = body;
            }
        }

        // Search for ProductVersion annotation
        var productVersionMatch = System.Text.RegularExpressions.Regex.Match(
            sourceCode,
            @"\.HasAnnotation\(""ProductVersion"",\s*""([^""]+)""\)");

        if (productVersionMatch.Success)
        {
            efCoreVersion = productVersionMatch.Groups[1].Value;
        }

        return new ParsedModelSnapshot
        {
            SnapshotClassName = snapshotClassName,
            DbContextName = dbContextName,
            Namespace = ns,
            EfCoreVersion = efCoreVersion,
            BuildModelBody = buildModelBody,
            Usings = usings
        };
    }

    private static (string? Body, bool HasReturn) ExtractMethodBody(MethodDeclarationSyntax method, string sourceCode)
    {
        if (method.Body != null)
        {
            var openBrace = method.Body.OpenBraceToken;
            var closeBrace = method.Body.CloseBraceToken;

            int start = openBrace.Span.End;
            int length = closeBrace.Span.Start - start;

            string content = length > 0
                ? sourceCode.Substring(start, length).Trim()
                : string.Empty;

            bool hasReturn = method.Body.DescendantNodes()
                .OfType<ReturnStatementSyntax>()
                .Any(r => !r.Ancestors().OfType<LocalFunctionStatementSyntax>().Any()
                       && !r.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().Any());

            return (string.IsNullOrWhiteSpace(content) ? null : content, hasReturn);
        }

        if (method.ExpressionBody != null)
        {
            var exprText = method.ExpressionBody.Expression.ToFullString().Trim();
            return ($"{exprText};", false);
        }

        return (null, false);
    }

    private static List<ReversedOperation> SynthesizeDownOperations(BlockSyntax upBody)
    {
        var reversedOps = new List<ReversedOperation>();

        // Look for invocation expressions on migrationBuilder
        var invocations = upBody.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Expression.ToString().Contains("migrationBuilder"))
            {
                var methodName = memberAccess.Name.Identifier.Text;
                var args = invocation.ArgumentList.Arguments;

                if (methodName == "CreateTable")
                {
                    var tableName = GetStringArgument(args, "name", 0);
                    if (!string.IsNullOrEmpty(tableName))
                    {
                        reversedOps.Add(new ReversedOperation
                        {
                            Code = $"migrationBuilder.DropTable(name: \"{tableName}\");",
                            IsDropTable = true
                        });
                    }
                }
                else if (methodName == "AddColumn")
                {
                    var colName = GetStringArgument(args, "name", 0);
                    var tableName = GetStringArgument(args, "table", 1);
                    if (!string.IsNullOrEmpty(colName) && !string.IsNullOrEmpty(tableName))
                    {
                        reversedOps.Add(new ReversedOperation
                        {
                            Code = $"migrationBuilder.DropColumn(name: \"{colName}\", table: \"{tableName}\");",
                            IsDropTable = false
                        });
                    }
                }
                else if (methodName == "CreateIndex")
                {
                    var indexName = GetStringArgument(args, "name", 0);
                    var tableName = GetStringArgument(args, "table", 1);
                    if (!string.IsNullOrEmpty(indexName) && !string.IsNullOrEmpty(tableName))
                    {
                        reversedOps.Add(new ReversedOperation
                        {
                            Code = $"migrationBuilder.DropIndex(name: \"{indexName}\", table: \"{tableName}\");",
                            IsDropTable = false
                        });
                    }
                }
            }
        }

        return reversedOps;
    }

    private static string? GetStringArgument(SeparatedSyntaxList<ArgumentSyntax> args, string name, int index)
    {
        var named = args.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == name);
        if (named != null)
        {
            return ExtractLiteralOrIdentifier(named.Expression);
        }

        if (index < args.Count && args[index].NameColon == null)
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
