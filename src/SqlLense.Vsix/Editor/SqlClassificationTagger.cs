using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
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

        [Import]
        internal SqlAliasClassifications Aliases { get; set; } = null!;

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
                () => new SqlClassificationTagger(buffer, Registry, Aliases)) as ITagger<T>;
        }
    }

    /// <summary>
    /// Colors SQL tokens inside C# strings. Tags are produced from the last computed regions and
    /// translated to the requested snapshot, so typing never waits on analysis. Alias colors replace
    /// the identifier color on the tokens they cover.
    /// </summary>
    internal sealed class SqlClassificationTagger : ITagger<IClassificationTag>
    {
        private readonly SqlRegionCache _cache;
        private readonly IClassificationTag?[] _tags;
        private readonly SqlAliasClassifications _aliases;

        public SqlClassificationTagger(ITextBuffer buffer, IClassificationTypeRegistryService registry, SqlAliasClassifications aliases)
        {
            _aliases = aliases;
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
            var textRange = new TextSpan(range.Start, range.Length);

            // Aliases whose color is not registered yet (see GetTag) keep the identifier color instead.
            var aliasSpans = SqlEditorServices.ClassifyAliases(current.Regions, textRange, SqlLenseSettings.AliasParts)
                .Select(alias => (alias.Span, Tag: _aliases.GetTag(alias.ColorIndex)))
                .Where(alias => alias.Tag != null)
                .ToList();
            foreach (var alias in aliasSpans)
            {
                if (Translate(alias.Span, sourceSnapshot, requestedSnapshot) is { } span)
                {
                    yield return new TagSpan<IClassificationTag>(span, alias.Tag!);
                }
            }

            // Both sequences are sorted by position, so one cursor finds the alias span covering a token.
            var next = 0;
            foreach (var classified in SqlEditorServices.Classify(current.Regions, textRange))
            {
                while (next < aliasSpans.Count && aliasSpans[next].Span.End <= classified.Span.Start)
                {
                    next++;
                }

                if (next < aliasSpans.Count && aliasSpans[next].Span.Contains(classified.Span))
                {
                    continue;
                }

                var tag = _tags[(int)classified.Class];
                if (tag != null && Translate(classified.Span, sourceSnapshot, requestedSnapshot) is { } span)
                {
                    yield return new TagSpan<IClassificationTag>(span, tag);
                }
            }
        }

        private static SnapshotSpan? Translate(TextSpan textSpan, ITextSnapshot source, ITextSnapshot requested)
        {
            if (textSpan.End > source.Length)
            {
                return null;
            }

            var span = new SnapshotSpan(source, textSpan.Start, textSpan.Length);
            if (source != requested)
            {
                span = span.TranslateTo(requested, SpanTrackingMode.EdgeExclusive);
                if (span.IsEmpty)
                {
                    return null;
                }
            }

            return span;
        }

        private static IClassificationTag? Tag(IClassificationTypeRegistryService registry, string name)
        {
            var type = registry.GetClassificationType(name);
            return type == null ? null : new ClassificationTag(type);
        }
    }
}
