using System;
using System.Globalization;

namespace SqlLense.Analyzers.CSharp
{
    internal enum LiteralFlavor
    {
        /// <summary><c>"..."</c> or the text of <c>$"..."</c>: backslash escapes.</summary>
        Regular,

        /// <summary><c>@"..."</c> or the text of <c>$@"..."</c>: doubled quotes.</summary>
        Verbatim,

        /// <summary>Raw strings (<c>"""</c>): no escapes, but indentation may be stripped.</summary>
        Raw,
    }

    /// <summary>
    /// Maps each character of a string literal's value back to its position in source, accounting for
    /// escape sequences, doubled quotes/braces and raw-string indentation removal.
    /// </summary>
    internal static class LiteralOffsets
    {
        /// <summary>
        /// Returns an array with one source offset (relative to <paramref name="source"/>) per value
        /// character plus a trailing end offset.
        /// </summary>
        /// <param name="value">The literal's value (ValueText).</param>
        /// <param name="source">The raw source text of the token.</param>
        /// <param name="contentStart">Where the content starts in <paramref name="source"/> (after quotes).</param>
        /// <param name="contentEnd">Where the content ends in <paramref name="source"/> (before quotes).</param>
        /// <param name="flavor">How escapes are written.</param>
        /// <param name="doubledBraces">True for interpolated text, where <c>{{</c>/<c>}}</c> are escapes.</param>
        public static int[] Compute(string value, string source, int contentStart, int contentEnd, LiteralFlavor flavor, bool doubledBraces)
        {
            var offsets = new int[value.Length + 1];
            var ok = flavor == LiteralFlavor.Raw
                ? AlignGreedy(value, source, contentStart, contentEnd, offsets)
                : Scan(value, source, contentStart, contentEnd, flavor, doubledBraces, offsets);

            if (!ok)
            {
                // Unexpected shape: degrade to a proportional mapping inside the content rather than fail.
                var span = Math.Max(0, contentEnd - contentStart);
                for (var i = 0; i <= value.Length; i++)
                {
                    offsets[i] = contentStart + (value.Length == 0 ? 0 : (int)((long)span * i / value.Length));
                }
            }

            return offsets;
        }

        private static bool Scan(string value, string source, int start, int end, LiteralFlavor flavor, bool doubledBraces, int[] offsets)
        {
            var j = start;
            var i = 0;
            while (i < value.Length)
            {
                if (j >= end)
                {
                    return false;
                }

                var c = source[j];
                int consumed;
                var produced = 1;

                if (flavor == LiteralFlavor.Regular && c == '\\' && j + 1 < end)
                {
                    switch (source[j + 1])
                    {
                        case 'u':
                            consumed = 6;
                            break;
                        case 'U':
                            consumed = 10;
                            if (j + 10 <= end &&
                                uint.TryParse(source.Substring(j + 2, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint) &&
                                codePoint > 0xFFFF)
                            {
                                produced = 2;
                            }

                            break;
                        case 'x':
                            consumed = 2;
                            while (consumed < 6 && j + consumed < end && Uri.IsHexDigit(source[j + consumed]))
                            {
                                consumed++;
                            }

                            break;
                        default:
                            consumed = 2;
                            break;
                    }
                }
                else if (flavor == LiteralFlavor.Verbatim && c == '"' && j + 1 < end && source[j + 1] == '"')
                {
                    consumed = 2;
                }
                else if (doubledBraces && (c == '{' || c == '}') && j + 1 < end && source[j + 1] == c)
                {
                    consumed = 2;
                }
                else
                {
                    consumed = 1;
                }

                for (var k = 0; k < produced && i < value.Length; k++)
                {
                    offsets[i++] = j;
                }

                j += consumed;
            }

            offsets[value.Length] = Math.Min(j, end);
            return true;
        }

        /// <summary>
        /// Raw strings only ever drop characters (indentation whitespace and the newlines next to the
        /// delimiters), so the value is a subsequence of the source and can be aligned greedily.
        /// </summary>
        private static bool AlignGreedy(string value, string source, int start, int end, int[] offsets)
        {
            var j = start;
            for (var i = 0; i < value.Length; i++)
            {
                while (j < end && source[j] != value[i])
                {
                    j++;
                }

                if (j >= end)
                {
                    return false;
                }

                offsets[i] = j++;
            }

            offsets[value.Length] = j;
            return true;
        }
    }
}
