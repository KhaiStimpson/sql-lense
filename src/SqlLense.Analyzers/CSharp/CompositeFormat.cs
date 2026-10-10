using System.Collections.Generic;
using System.Text;

namespace SqlLense.Analyzers.CSharp
{
    /// <summary>
    /// Splits a .NET composite format string (<c>string.Format</c>, <c>AppendFormat</c>, EF Core's
    /// <c>FromSqlRaw</c>) into text runs and <c>{index[,alignment][:format]}</c> holes.
    /// </summary>
    internal static class CompositeFormat
    {
        internal readonly struct Item
        {
            private Item(string? text, int[]? valueMap, int start, int end, int argumentIndex)
            {
                Text = text;
                ValueMap = valueMap;
                Start = start;
                End = end;
                ArgumentIndex = argumentIndex;
            }

            /// <summary>The unescaped text of a run, or null for a hole.</summary>
            public string? Text { get; }

            /// <summary>For a run: the index in the format string of each character, plus an end index.</summary>
            public int[]? ValueMap { get; }

            /// <summary>Where the item starts in the format string.</summary>
            public int Start { get; }

            /// <summary>Where the item ends (exclusive) in the format string.</summary>
            public int End { get; }

            /// <summary>For a hole: the argument index; -1 for a run.</summary>
            public int ArgumentIndex { get; }

            public bool IsHole => Text == null;

            public static Item Run(string text, int[] valueMap, int start, int end) => new Item(text, valueMap, start, end, -1);

            public static Item Hole(int argumentIndex, int start, int end) => new Item(null, null, start, end, argumentIndex);
        }

        /// <summary>Cheap pre-check: an opening brace followed by a digit.</summary>
        public static bool MayContainHole(string value)
        {
            var index = value.IndexOf('{');
            while (index >= 0 && index + 1 < value.Length)
            {
                if (value[index + 1] >= '0' && value[index + 1] <= '9')
                {
                    return true;
                }

                index = value.IndexOf('{', index + 1);
            }

            return false;
        }

        /// <summary>
        /// Parses <paramref name="value"/>. Returns false when it is not a well-formed composite format
        /// string (for example a lone brace), in which case it should be used as plain text.
        /// </summary>
        public static bool TryParse(string value, List<Item> items)
        {
            items.Clear();
            var text = new StringBuilder();
            var map = new List<int>();
            var runStart = 0;
            var i = 0;
            while (i < value.Length)
            {
                var c = value[i];
                if (c == '{' && i + 1 < value.Length && value[i + 1] == '{')
                {
                    map.Add(i);
                    text.Append('{');
                    i += 2;
                    continue;
                }

                if (c == '}')
                {
                    if (i + 1 < value.Length && value[i + 1] == '}')
                    {
                        map.Add(i);
                        text.Append('}');
                        i += 2;
                        continue;
                    }

                    return false;
                }

                if (c != '{')
                {
                    map.Add(i);
                    text.Append(c);
                    i++;
                    continue;
                }

                var holeStart = i;
                if (!TryParseHole(value, ref i, out var argumentIndex))
                {
                    return false;
                }

                AddRun(items, text, map, runStart, holeStart);
                items.Add(Item.Hole(argumentIndex, holeStart, i));
                runStart = i;
            }

            AddRun(items, text, map, runStart, value.Length);
            return true;
        }

        private static void AddRun(List<Item> items, StringBuilder text, List<int> map, int start, int end)
        {
            if (text.Length > 0)
            {
                map.Add(end);
                items.Add(Item.Run(text.ToString(), map.ToArray(), start, end));
            }

            text.Clear();
            map.Clear();
        }

        /// <summary>Parses <c>{index[,alignment][:format]}</c> starting at the opening brace.</summary>
        private static bool TryParseHole(string value, ref int i, out int argumentIndex)
        {
            argumentIndex = 0;
            var j = i + 1;
            var digits = 0;
            while (j < value.Length && value[j] >= '0' && value[j] <= '9')
            {
                if (digits++ > 6)
                {
                    return false;
                }

                argumentIndex = (argumentIndex * 10) + (value[j] - '0');
                j++;
            }

            if (digits == 0)
            {
                return false;
            }

            SkipSpaces(value, ref j);
            if (j < value.Length && value[j] == ',')
            {
                j++;
                SkipSpaces(value, ref j);
                if (j < value.Length && value[j] == '-')
                {
                    j++;
                }

                var alignmentDigits = 0;
                while (j < value.Length && value[j] >= '0' && value[j] <= '9')
                {
                    alignmentDigits++;
                    j++;
                }

                if (alignmentDigits == 0)
                {
                    return false;
                }

                SkipSpaces(value, ref j);
            }

            if (j < value.Length && value[j] == ':')
            {
                j++;
                while (j < value.Length && value[j] != '}')
                {
                    if (value[j] == '{')
                    {
                        return false;
                    }

                    j++;
                }
            }

            if (j >= value.Length || value[j] != '}')
            {
                return false;
            }

            i = j + 1;
            return true;
        }

        private static void SkipSpaces(string value, ref int j)
        {
            while (j < value.Length && value[j] == ' ')
            {
                j++;
            }
        }
    }
}
