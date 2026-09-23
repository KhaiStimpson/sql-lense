using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using SqlLense.Analyzers.CSharp;
using SqlLense.Sql;

namespace SqlLense.CodeFixes
{
    /// <summary>
    /// Table, column, schema and procedure completion inside SQL strings. Returns immediately (without
    /// touching the semantic model) whenever the caret is not inside a string that starts like SQL.
    /// </summary>
    [ExportCompletionProvider(nameof(SqlCompletionProvider), LanguageNames.CSharp), Shared]
    public sealed class SqlCompletionProvider : CompletionProvider
    {
        private const string InsertTextProperty = "SqlLense.InsertText";
        private const string DetailProperty = "SqlLense.Detail";

        public override bool ShouldTriggerCompletion(SourceText text, int caretPosition, CompletionTrigger trigger, OptionSet options)
        {
            if (trigger.Kind == CompletionTriggerKind.Invoke || trigger.Kind == CompletionTriggerKind.InvokeAndCommitIfUnique)
            {
                return true;
            }

            return trigger.Kind == CompletionTriggerKind.Insertion &&
                (char.IsLetter(trigger.Character) || trigger.Character == '.' || trigger.Character == '[' || trigger.Character == '_');
        }

        public override async Task ProvideCompletionsAsync(CompletionContext context)
        {
            var document = context.Document;
            var position = context.Position;
            var cancellationToken = context.CancellationToken;

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root == null)
            {
                return;
            }

            // Cheap syntactic gate: the character before the caret must be string content.
            if (position == 0 || !IsStringContentToken(root.FindToken(position - 1)))
            {
                return;
            }

            var expression = SqlStringExtractor.FindRootAt(root, position);
            if (expression == null)
            {
                return;
            }

            var candidate = SqlStringExtractor.TryExtract(expression, semanticModel: null, SqlDetectionMode.Lenient, cancellationToken);
            if (candidate == null)
            {
                return;
            }

            var sqlOffset = candidate.Map.MapToSql(position);
            var schema = sqlOffset < 0 ? null : WorkspaceSchema.GetSchema(document);
            if (schema == null)
            {
                return;
            }

            var result = SqlCompletionEngine.GetCompletions(candidate.Sql, sqlOffset, schema);
            if (result == null || result.Items.Count == 0)
            {
                return;
            }

            var span = candidate.Map.MapSpan(result.ReplaceStart, result.ReplaceLength);
            context.CompletionListSpan = span.Length == 0 ? new TextSpan(position, 0) : span;
            context.IsExclusive = true;

            foreach (var item in result.Items)
            {
                var properties = ImmutableDictionary<string, string>.Empty
                    .Add(InsertTextProperty, item.InsertText)
                    .Add(DetailProperty, item.Detail ?? string.Empty);
                context.AddItem(CompletionItem.Create(
                    item.DisplayText,
                    filterText: item.DisplayText,
                    sortText: item.SortText,
                    properties: properties,
                    tags: ImmutableArray.Create(TagFor(item.Kind)),
                    inlineDescription: item.Detail));
            }
        }

        public override Task<CompletionDescription?> GetDescriptionAsync(Document document, CompletionItem item, CancellationToken cancellationToken) =>
            Task.FromResult<CompletionDescription?>(
                item.Properties.TryGetValue(DetailProperty, out var detail) && !string.IsNullOrEmpty(detail)
                    ? CompletionDescription.FromText(detail)
                    : CompletionDescription.Empty);

        public override Task<CompletionChange> GetChangeAsync(Document document, CompletionItem item, char? commitKey, CancellationToken cancellationToken)
        {
            var insert = item.Properties.TryGetValue(InsertTextProperty, out var text) ? text : item.DisplayText;
            return Task.FromResult(CompletionChange.Create(new TextChange(item.Span, insert)));
        }

        private static bool IsStringContentToken(SyntaxToken token) =>
            token.IsKind(SyntaxKind.StringLiteralToken) ||
            token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken) ||
            token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken) ||
            token.IsKind(SyntaxKind.InterpolatedStringTextToken);

        private static string TagFor(SqlCompletionKind kind) => kind switch
        {
            SqlCompletionKind.Table => WellKnownTags.Class,
            SqlCompletionKind.View => WellKnownTags.Structure,
            SqlCompletionKind.Column => WellKnownTags.Field,
            SqlCompletionKind.Procedure => WellKnownTags.Method,
            SqlCompletionKind.Function => WellKnownTags.Method,
            SqlCompletionKind.Schema => WellKnownTags.Namespace,
            _ => WellKnownTags.Local,
        };
    }
}
