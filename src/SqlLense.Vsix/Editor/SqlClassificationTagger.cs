using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using SqlLense.CodeFixes;
using SqlLense.Sql;
using SqlLense.Vsix.Options;
using TextSpan = Microsoft.CodeAnalysis.Text.TextSpan;

namespace SqlLense.Vsix.Editor
{
    [Export(typeof(IViewTaggerProvider))]
    [ContentType("CSharp")]
    [TagType(typeof(IClassificationTag))]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class SqlClassificationTaggerProvider : IViewTaggerProvider
    {
        [Import]
        internal IClassificationTypeRegistryService Registry { get; set; } = null!;

        public ITagger<T>? CreateTagger<T>(ITextView textView, ITextBuffer buffer)
            where T : ITag
        {
            // Only the top-level C# buffer; projection buffers (Razor etc.) are out of scope for now.
            if (textView.TextBuffer != buffer)
            {
                return null;
            }

            return buffer.Properties.GetOrCreateSingletonProperty(
                typeof(SqlClassificationTagger),
                () => new SqlClassificationTagger(buffer, Registry)) as ITagger<T>;
        }
    }

    /// <summary>
    /// Colors SQL tokens inside C# strings. Tags are produced from the last computed regions and
    /// translated to the requested snapshot, so typing never waits on analysis.
    /// </summary>
    internal sealed class SqlClassificationTagger : ITagger<IClassificationTag>
    {
        private readonly SqlRegionCache _cache;
        private readonly IClassificationTag?[] _tags;

        public SqlClassificationTagger(ITextBuffer buffer, IClassificationTypeRegistryService registry)
        {
            _tags = new IClassificationTag?[Enum.GetValues(typeof(SqlTokenClass)).Length];
            _tags[(int)SqlTokenClass.Keyword] = Tag(registry, SqlClassificationDefinitions.Keyword);
            _tags[(int)SqlTokenClass.Identifier] = Tag(registry, SqlClassificationDefinitions.Identifier);
            _tags[(int)SqlTokenClass.Function] = Tag(registry, SqlClassificationDefinitions.Function);
            _tags[(int)SqlTokenClass.Variable] = Tag(registry, SqlClassificationDefinitions.Variable);
            _tags[(int)SqlTokenClass.Number] = Tag(registry, SqlClassificationDefinitions.Number);
            _tags[(int)SqlTokenClass.Comment] = Tag(registry, SqlClassificationDefinitions.Comment);

            _cache = SqlRegionCache.For(buffer);
            _cache.Changed += (_, e) => TagsChanged?.Invoke(this, e);
        }

        public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

        public IEnumerable<ITagSpan<IClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0 || !SqlLenseSettings.HighlightingEnabled)
            {
                yield break;
            }

            var current = _cache.Current;
            if (current == null || current.Regions.Candidates.Count == 0)
            {
                yield break;
            }

            var requestedSnapshot = spans[0].Snapshot;
            var sourceSnapshot = current.Snapshot;
            if (requestedSnapshot.TextBuffer != sourceSnapshot.TextBuffer)
            {
                yield break;
            }

            // Translate the requested range back to the analyzed snapshot.
            var requested = new SnapshotSpan(spans[0].Start, spans[spans.Count - 1].End);
            var range = requested.TranslateTo(sourceSnapshot, SpanTrackingMode.EdgeInclusive);

            foreach (var classified in SqlEditorServices.Classify(current.Regions, new TextSpan(range.Start, range.Length)))
            {
                var tag = _tags[(int)classified.Class];
                if (tag == null || classified.Span.End > sourceSnapshot.Length)
                {
                    continue;
                }

                var span = new SnapshotSpan(sourceSnapshot, classified.Span.Start, classified.Span.Length);
                if (sourceSnapshot != requestedSnapshot)
                {
                    span = span.TranslateTo(requestedSnapshot, SpanTrackingMode.EdgeExclusive);
                    if (span.IsEmpty)
                    {
                        continue;
                    }
                }

                yield return new TagSpan<IClassificationTag>(span, tag);
            }
        }

        private static IClassificationTag? Tag(IClassificationTypeRegistryService registry, string name)
        {
            var type = registry.GetClassificationType(name);
            return type == null ? null : new ClassificationTag(type);
        }
    }
}
