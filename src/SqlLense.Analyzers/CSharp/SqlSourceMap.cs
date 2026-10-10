using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace SqlLense.Analyzers.CSharp
{
    /// <summary>
    /// Maps offsets in the extracted SQL text back to positions in the C# source and vice versa.
    /// Character-level maps for literals are computed lazily: most SQL strings produce no
    /// diagnostics, so most maps are never materialized.
    /// </summary>
    public sealed class SqlSourceMap
    {
        private readonly List<Segment> _segments;

        internal SqlSourceMap(List<Segment> segments)
        {
            _segments = segments;
            if (segments.Count > 0)
            {
                var start = int.MaxValue;
                var end = 0;
                foreach (var segment in segments)
                {
                    start = Math.Min(start, segment.SourceSpan.Start);
                    end = Math.Max(end, segment.SourceSpan.End);
                }

                Bounds = TextSpan.FromBounds(start, end);
            }
        }

        /// <summary>The smallest span covering every piece of source the SQL comes from (inlined locals included).</summary>
        public TextSpan Bounds { get; }

        /// <summary>The source span covering all SQL text.</summary>
        public TextSpan FullSpan =>
            _segments.Count == 0 ? default : TextSpan.FromBounds(_segments[0].SourceSpan.Start, _segments[_segments.Count - 1].SourceSpan.End);

        /// <summary>
        /// Maps a SQL range to a source span. Ranges that cross segments of one expression cover everything
        /// in between; ranges that run into another statement's fragment are clipped to the first one.
        /// </summary>
        public TextSpan MapSpan(int sqlStart, int sqlLength)
        {
            if (_segments.Count == 0)
            {
                return default;
            }

            var start = MapPosition(sqlStart, isEnd: false);
            var endOffset = sqlStart + Math.Max(sqlLength, 0);
            var first = FindSegment(sqlStart);
            var last = FindSegment(endOffset > 0 ? endOffset - 1 : endOffset);
            int end;
            if (first != null && last != null && first.Group != last.Group)
            {
                end = LastSourceEndInGroup(first);
            }
            else
            {
                end = MapPosition(endOffset, isEnd: true);
            }

            return end > start ? TextSpan.FromBounds(start, end) : new TextSpan(start, 0);
        }

        /// <summary>
        /// True when the SQL offset lies in text inlined from another SQL string that is analyzed (and
        /// reported) on its own, so reporting or coloring it again here would duplicate it.
        /// </summary>
        public bool IsReportedElsewhere(int sqlOffset)
        {
            var segment = FindSegment(sqlOffset);
            return segment != null && segment.ReportedElsewhere;
        }

        private int LastSourceEndInGroup(Segment first)
        {
            var index = _segments.IndexOf(first);
            var end = first.IsLiteral ? first.SourceSpan.Start + first.GetOffsets()[first.SqlLength] : first.SourceSpan.End;
            for (var i = index + 1; i < _segments.Count && _segments[i].Group == first.Group; i++)
            {
                var s = _segments[i];
                end = s.IsLiteral ? s.SourceSpan.Start + s.GetOffsets()[s.SqlLength] : s.SourceSpan.End;
            }

            return end;
        }

        /// <summary>
        /// Maps a SQL range that lies entirely inside one literal segment. Used for highlighting, where a
        /// token straddling a concatenation boundary or an interpolation hole must not be colored.
        /// </summary>
        public bool TryMapWithinLiteral(int sqlStart, int sqlLength, out TextSpan span)
        {
            span = default;
            var segment = FindSegment(sqlStart);
            if (segment == null || !segment.IsLiteral || segment.ReportedElsewhere || sqlLength <= 0 ||
                sqlStart + sqlLength > segment.SqlStart + segment.SqlLength)
            {
                return false;
            }

            var offsets = segment.GetOffsets();
            var start = segment.SourceSpan.Start + offsets[sqlStart - segment.SqlStart];
            var end = segment.SourceSpan.Start + offsets[sqlStart - segment.SqlStart + sqlLength];
            if (end <= start)
            {
                return false;
            }

            span = TextSpan.FromBounds(start, end);
            return true;
        }

        /// <summary>
        /// Maps a source position inside one of the literal segments to a SQL offset, or -1 when the
        /// position is not inside SQL text (e.g. inside an interpolation hole).
        /// </summary>
        public int MapToSql(int sourcePosition)
        {
            foreach (var segment in _segments)
            {
                if (!segment.IsLiteral || sourcePosition < segment.SourceSpan.Start || sourcePosition > segment.SourceSpan.End)
                {
                    continue;
                }

                var offsets = segment.GetOffsets();
                var relative = sourcePosition - segment.SourceSpan.Start;

                // First value index whose source offset is >= the position.
                int lo = 0, hi = offsets.Length - 1;
                while (lo < hi)
                {
                    var mid = (lo + hi) >> 1;
                    if (offsets[mid] < relative)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid;
                    }
                }

                if (offsets[lo] < relative)
                {
                    continue; // past the end of this segment's content (closing quotes)
                }

                if (relative < offsets[0])
                {
                    continue; // on the opening quotes
                }

                return segment.SqlStart + lo;
            }

            return -1;
        }

        /// <summary>True when the SQL range maps to one literal segment whose source text is identical.</summary>
        public bool IsVerbatim(int sqlStart, int sqlLength, string sql, SourceText source)
        {
            var segment = FindSegment(sqlStart);
            if (segment == null || !segment.IsLiteral || sqlStart + sqlLength > segment.SqlStart + segment.SqlLength)
            {
                return false;
            }

            var span = MapSpan(sqlStart, sqlLength);
            return span.Length == sqlLength && span.End <= source.Length &&
                string.CompareOrdinal(source.ToString(span), sql.Substring(sqlStart, sqlLength)) == 0;
        }

        private int MapPosition(int sqlOffset, bool isEnd)
        {
            var segment = FindSegment(isEnd && sqlOffset > 0 ? sqlOffset - 1 : sqlOffset) ?? _segments[_segments.Count - 1];
            if (!segment.IsLiteral)
            {
                return isEnd ? segment.SourceSpan.End : segment.SourceSpan.Start;
            }

            var offsets = segment.GetOffsets();
            // offsets[i + 1] is where character i's (possibly multi-character) escape ends, so the same
            // lookup serves both as a start and as an end position.
            var local = Math.Max(0, Math.Min(sqlOffset - segment.SqlStart, segment.SqlLength));
            return segment.SourceSpan.Start + offsets[local];
        }

        private Segment? FindSegment(int sqlOffset)
        {
            int lo = 0, hi = _segments.Count - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                var s = _segments[mid];
                if (sqlOffset < s.SqlStart)
                {
                    hi = mid - 1;
                }
                else if (sqlOffset >= s.SqlStart + s.SqlLength && !(s.SqlLength == 0 && sqlOffset == s.SqlStart))
                {
                    lo = mid + 1;
                }
                else
                {
                    return s;
                }
            }

            return null;
        }

        internal sealed class Segment
        {
            private readonly SyntaxToken _token;
            private readonly LiteralFlavor _flavor;
            private readonly bool _doubledBraces;
            private readonly int _valueStart;
            private readonly int[]? _valueMap;
            private int[]? _offsets;

            private Segment(
                int sqlStart, int sqlLength, TextSpan sourceSpan, SyntaxToken token, LiteralFlavor flavor, bool doubledBraces,
                bool isLiteral, int valueStart, int[]? valueMap, int group, bool reportedElsewhere)
            {
                SqlStart = sqlStart;
                SqlLength = sqlLength;
                SourceSpan = sourceSpan;
                _token = token;
                _flavor = flavor;
                _doubledBraces = doubledBraces;
                IsLiteral = isLiteral;
                _valueStart = valueStart;
                _valueMap = valueMap;
                Group = group;
                ReportedElsewhere = reportedElsewhere;
            }

            /// <summary>Segments from one C# expression share a group; separate statements of a built-up string do not.</summary>
            public int Group { get; }

            public bool ReportedElsewhere { get; }

            public int SqlStart { get; }

            public int SqlLength { get; }

            public TextSpan SourceSpan { get; }

            /// <summary>False for placeholders and constants from elsewhere: they map to their whole span.</summary>
            public bool IsLiteral { get; }

            /// <summary>
            /// SQL text taken from a literal token. The text is either <paramref name="valueStart"/>.. of the
            /// token's value, or (for unescaped format strings) described by <paramref name="valueMap"/>: the
            /// value index of each SQL character plus an end index.
            /// </summary>
            public static Segment Literal(
                int sqlStart, SyntaxToken token, string text, LiteralFlavor flavor, bool doubledBraces,
                int valueStart, int[]? valueMap, int group, bool reportedElsewhere) =>
                new Segment(sqlStart, text.Length, token.Span, token, flavor, doubledBraces, isLiteral: true, valueStart, valueMap, group, reportedElsewhere);

            public static Segment Opaque(int sqlStart, int sqlLength, TextSpan sourceSpan, int group, bool reportedElsewhere) =>
                new Segment(sqlStart, sqlLength, sourceSpan, default, default, false, isLiteral: false, 0, null, group, reportedElsewhere);

            /// <summary>Offsets relative to <see cref="SourceSpan"/>.Start, one per SQL char plus an end offset.</summary>
            public int[] GetOffsets()
            {
                var offsets = _offsets;
                if (offsets != null)
                {
                    return offsets;
                }

                offsets = ComputeTokenOffsets(_token, _flavor, _doubledBraces);
                if (_valueMap != null)
                {
                    var mapped = new int[SqlLength + 1];
                    for (var i = 0; i <= SqlLength; i++)
                    {
                        mapped[i] = offsets[Math.Min(_valueMap[i], offsets.Length - 1)];
                    }

                    offsets = mapped;
                }
                else if (_valueStart != 0 || SqlLength + 1 != offsets.Length)
                {
                    var sliced = new int[SqlLength + 1];
                    Array.Copy(offsets, _valueStart, sliced, 0, Math.Min(sliced.Length, offsets.Length - _valueStart));
                    offsets = sliced;
                }

                Interlocked.CompareExchange(ref _offsets, offsets, null);
                return _offsets!;
            }
        }

        /// <summary>Source offsets (relative to the token start) of each value character, plus an end offset.</summary>
        internal static int[] ComputeTokenOffsets(SyntaxToken token, LiteralFlavor flavor, bool doubledBraces)
        {
            var text = token.Text;
            GetContentBounds(token, text, out var contentStart, out var contentEnd);
            return LiteralOffsets.Compute(token.ValueText, text, contentStart, contentEnd, flavor, doubledBraces);
        }

        private static void GetContentBounds(SyntaxToken token, string text, out int start, out int end)
        {
            start = 0;
            end = text.Length;
            switch (token.Kind())
            {
                case SyntaxKind.StringLiteralToken:
                    start = text.StartsWith("@", StringComparison.Ordinal) ? 2 : 1;
                    end = Math.Max(start, text.Length - 1);
                    break;
                case SyntaxKind.SingleLineRawStringLiteralToken:
                case SyntaxKind.MultiLineRawStringLiteralToken:
                    var quotes = 0;
                    while (quotes < text.Length && text[quotes] == '"')
                    {
                        quotes++;
                    }

                    start = quotes;
                    end = Math.Max(start, text.Length - quotes);
                    break;
            }
        }
    }
}
