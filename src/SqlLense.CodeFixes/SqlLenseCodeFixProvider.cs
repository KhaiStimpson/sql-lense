using System;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SqlLense.Analyzers;
using SqlLense.Analyzers.CSharp;

namespace SqlLense.CodeFixes
{
    /// <summary>
    /// "Did you mean ...?" replacements for unknown names, and "Ignore SQL in this string" for any
    /// SqlLense diagnostic.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SqlLenseCodeFixProvider)), Shared]
    public sealed class SqlLenseCodeFixProvider : CodeFixProvider
    {
        public const string IgnoreEquivalenceKey = "SqlLense.Ignore";
        public const string IgnoreComment = "/* sqllense:ignore */";

        public override ImmutableArray<string> FixableDiagnosticIds => Descriptors.All.Select(d => d.Id).ToImmutableArray();

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var text = await context.Document.GetTextAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null)
            {
                return;
            }

            foreach (var diagnostic in context.Diagnostics)
            {
                RegisterRenames(context, diagnostic, text);

                var expression = SqlStringExtractor.FindRootAt(root, diagnostic.Location.SourceSpan.Start);
                if (expression != null)
                {
                    context.RegisterCodeFix(
                        CodeAction.Create(
                            "Ignore SQL in this string",
                            ct => AddIgnoreCommentAsync(context.Document, expression.SpanStart, ct),
                            IgnoreEquivalenceKey),
                        diagnostic);
                }
            }
        }

        private static void RegisterRenames(CodeFixContext context, Diagnostic diagnostic, SourceText text)
        {
            if (!diagnostic.Properties.TryGetValue(Descriptors.SuggestionsProperty, out var joined) || string.IsNullOrEmpty(joined) ||
                !diagnostic.Properties.TryGetValue(Descriptors.NameProperty, out var name) || name == null)
            {
                return;
            }

            var span = diagnostic.Location.SourceSpan;
            var written = text.ToString(span);
            var bracketed = written.Length >= 2 && written[0] == '[' && written[written.Length - 1] == ']';
            var unquoted = bracketed ? written.Substring(1, written.Length - 2) : written;

            // Only offer the rename when the name is written plainly in the literal (no escapes, not a
            // constant defined elsewhere), so replacing the span is guaranteed to be correct.
            if (!string.Equals(unquoted, name, StringComparison.Ordinal))
            {
                return;
            }

            foreach (var suggestion in joined!.Split('|'))
            {
                var replacement = bracketed
                    ? string.Join(".", suggestion.Split('.').Select(part => "[" + part + "]"))
                    : suggestion;
                context.RegisterCodeFix(
                    CodeAction.Create(
                        $"Change to '{suggestion}'",
                        ct => Task.FromResult(context.Document.WithText(text.WithChanges(new TextChange(span, replacement)))),
                        "SqlLense.Rename." + suggestion),
                    diagnostic);
            }
        }

        private static async Task<Document> AddIgnoreCommentAsync(Document document, int position, CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root == null)
            {
                return document;
            }

            // Re-resolve by position so batch fix-all works on the updated tree.
            var expression = SqlStringExtractor.FindRootAt(root, position);
            if (expression == null)
            {
                return document;
            }

            var first = expression.GetFirstToken();
            if (first.LeadingTrivia.ToString().IndexOf("sqllense:ignore", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return document;
            }

            var updated = first.WithLeadingTrivia(first.LeadingTrivia
                .Add(SyntaxFactory.Comment(IgnoreComment))
                .Add(SyntaxFactory.Space));
            return document.WithSyntaxRoot(root.ReplaceToken(first, updated));
        }
    }
}
