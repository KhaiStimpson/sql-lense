using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using SqlLense.Analyzers.CSharp;
using SqlLense.Schema;
using SqlLense.Sql;

namespace SqlLense.Analyzers
{
    /// <summary>
    /// Reports SQL syntax errors and, when a schema snapshot exists, unknown tables, columns and
    /// procedures inside C# string literals.
    /// </summary>
    /// <remarks>
    /// Performance: the per-string work is a keyword prefix check for the vast majority of strings.
    /// Only strings that look like SQL are parsed, parsing and validation are memoized by SQL text,
    /// and the schema is loaded once per snapshot file change.
    /// </remarks>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SqlLenseAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => Descriptors.All;

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            var globalOptions = context.Options.AnalyzerConfigOptionsProvider.GlobalOptions;
            if (!SchemaResolver.IsEnabled(globalOptions))
            {
                return;
            }

            var schema = SchemaResolver.GetSchema(globalOptions, context.Compilation);
            context.RegisterSyntaxNodeAction(
                c => AnalyzeExpression(c, schema),
                SyntaxKind.StringLiteralExpression,
                SyntaxKind.InterpolatedStringExpression,
                SyntaxKind.AddExpression);
        }

        private static void AnalyzeExpression(SyntaxNodeAnalysisContext context, DatabaseSchema? schema)
        {
            var expression = (ExpressionSyntax)context.Node;
            if (!SqlStringExtractor.IsRootStringExpression(expression))
            {
                return;
            }

            var candidate = SqlStringExtractor.TryExtract(expression, context.SemanticModel, SqlDetectionMode.Diagnostics, context.CancellationToken);
            if (candidate == null)
            {
                return;
            }

            var result = SqlEngine.Analyze(candidate.Sql, schema);
            if (result.Diagnostics.Count == 0)
            {
                return;
            }

            var tree = expression.SyntaxTree;
            foreach (var diagnostic in result.Diagnostics)
            {
                var span = candidate.Map.MapSpan(diagnostic.Start, diagnostic.Length);
                if (span.Length == 0)
                {
                    span = expression.Span;
                }

                var properties = ImmutableDictionary<string, string?>.Empty;
                if (diagnostic.Name != null)
                {
                    properties = properties.Add(Descriptors.NameProperty, diagnostic.Name);
                }

                if (diagnostic.Suggestions.Count > 0)
                {
                    properties = properties.Add(Descriptors.SuggestionsProperty, string.Join("|", diagnostic.Suggestions));
                }

                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.For(diagnostic.Kind),
                    Location.Create(tree, span),
                    properties,
                    diagnostic.Message));
            }
        }
    }
}
