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

        internal SqlSourceMap(List<Segment> segments) => _segments = segments;

        /// <summary>The source span covering all SQL text.</summary>
        public TextSpan FullSpan =>
            _segments.Count == 0 ? default : TextSpan.FromBounds(_segments[0].SourceSpan.Start, _segments[_segments.Count - 1].SourceSpan.End);

        /// <summary>Maps a SQL range to a source span. Ranges that cross segments cover everything in between.</summary>
        public TextSpan MapSpan(int sqlStart, int sqlLength)
        {
            if (_segments.Count == 0)
            {
                return default;
            }

            var start = MapPosition(sqlStart, isEnd: false);
            var end = MapPosition(sqlStart + Math.Max(sqlLength, 0), isEnd: true);
            return end > start ? TextSpan.FromBounds(start, end) : new TextSpan(start, 0);
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
            private int[]? _offsets;

            private Segment(int sqlStart, int sqlLength, TextSpan sourceSpan, SyntaxToken token, LiteralFlavor flavor, bool doubledBraces, bool isLiteral)
            {
                SqlStart = sqlStart;
                SqlLength = sqlLength;
                SourceSpan = sourceSpan;
                _token = token;
                _flavor = flavor;
                _doubledBraces = doubledBraces;
                IsLiteral = isLiteral;
            }

            public int SqlStart { get; }

            public int SqlLength { get; }

            public TextSpan SourceSpan { get; }

            /// <summary>False for placeholders and constants from elsewhere: they map to their whole span.</summary>
            public bool IsLiteral { get; }

            public static Segment Literal(int sqlStart, SyntaxToken token, string value, LiteralFlavor flavor, bool doubledBraces) =>
                new Segment(sqlStart, value.Length, token.Span, token, flavor, doubledBraces, isLiteral: true);

            public static Segment Opaque(int sqlStart, int sqlLength, TextSpan sourceSpan) =>
                new Segment(sqlStart, sqlLength, sourceSpan, default, default, false, isLiteral: false);

            /// <summary>Offsets relative to <see cref="SourceSpan"/>.Start, one per SQL char plus an end offset.</summary>
            public int[] GetOffsets()
            {
                var offsets = _offsets;
                if (offsets != null)
                {
                    return offsets;
                }

                var text = _token.Text;
                var value = _token.ValueText;
                GetContentBounds(text, out var contentStart, out var contentEnd);
                offsets = LiteralOffsets.Compute(value, text, contentStart, contentEnd, _flavor, _doubledBraces);
                Interlocked.CompareExchange(ref _offsets, offsets, null);
                return _offsets!;
            }

            private void GetContentBounds(string text, out int start, out int end)
            {
                start = 0;
                end = text.Length;
                switch (_token.Kind())
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
}
