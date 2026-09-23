using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Utilities;
using SqlLense.CodeFixes;
using SqlLense.Vsix.Options;

namespace SqlLense.Vsix.Editor
{
    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [Name("SqlLense SQL Quick Info")]
    [ContentType("CSharp")]
    internal sealed class SqlQuickInfoSourceProvider : IAsyncQuickInfoSourceProvider
    {
        public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer) =>
            textBuffer.Properties.GetOrCreateSingletonProperty(typeof(SqlQuickInfoSource), () => new SqlQuickInfoSource(textBuffer));
    }

    /// <summary>Shows table, column and procedure details when hovering SQL inside C# strings.</summary>
    internal sealed class SqlQuickInfoSource : IAsyncQuickInfoSource
    {
        private readonly ITextBuffer _buffer;

        public SqlQuickInfoSource(ITextBuffer buffer) => _buffer = buffer;

        public async Task<QuickInfoItem?> GetQuickInfoItemAsync(IAsyncQuickInfoSession session, CancellationToken cancellationToken)
        {
            if (!SqlLenseSettings.QuickInfoEnabled)
            {
                return null;
            }

            var point = session.GetTriggerPoint(_buffer.CurrentSnapshot);
            if (point == null)
            {
                return null;
            }

            var snapshot = point.Value.Snapshot;
            var regions = await SqlRegionCache.For(_buffer).GetForSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
            if (regions == null || regions.Regions.FindAt(point.Value.Position) == null)
            {
                return null;
            }

            var schema = WorkspaceSchema.GetSchema(regions.Document);
            if (schema == null)
            {
                return null;
            }

            var info = SqlEditorServices.GetQuickInfo(regions.Regions, point.Value.Position, schema);
            if (info == null || info.Span.End > snapshot.Length)
            {
                return null;
            }

            var lines = info.Description.Split('\n');
            var header = new ClassifiedTextElement(
                new ClassifiedTextRun(PredefinedClassificationTypeNames.Keyword, lines[0].Substring(0, lines[0].IndexOf(')') + 1)),
                new ClassifiedTextRun(PredefinedClassificationTypeNames.Identifier, lines[0].Substring(lines[0].IndexOf(')') + 1)));
            var body = lines.Skip(1).Select(line => (object)new ClassifiedTextElement(
                new ClassifiedTextRun(PredefinedClassificationTypeNames.NaturalLanguage, line)));
            var content = new ContainerElement(ContainerElementStyle.Stacked, new object[] { header }.Concat(body));

            var applicableTo = snapshot.CreateTrackingSpan(info.Span.Start, info.Span.Length, SpanTrackingMode.EdgeInclusive);
            return new QuickInfoItem(applicableTo, content);
        }

        public void Dispose()
        {
        }
    }
}
