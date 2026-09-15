using System;
using System.Collections.Generic;
using System.Globalization;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>
    /// The characters a glyph may be drawn from, written as one regular-expression
    /// character class such as <c>[a-zA-Z0-9]</c>.
    /// </summary>
    /// <remarks>
    /// Only the bracket form is accepted: literal characters, inclusive ranges, and the
    /// escapes <c>\\ \[ \] \- \^</c> and <c>\uXXXX</c>. A <c>-</c> first or last in the
    /// class is literal. Negation is rejected, because the complement of a class is most
    /// of Unicode and no font covers it.
    ///
    /// The set is kept as sorted, merged ranges rather than a list of characters, so a
    /// CJK block of twenty thousand characters costs one range.
    /// </remarks>
    public sealed class GlyphCharacterSet
    {
        private readonly int[] starts;
        private readonly int[] ends;

        // Characters in all ranges before each range, for indexing into the set.
        private readonly int[] offsets;

        private GlyphCharacterSet(string pattern, List<(int start, int end)> ranges)
        {
            Pattern = pattern;
            starts = new int[ranges.Count];
            ends = new int[ranges.Count];
            offsets = new int[ranges.Count];
            int count = 0;
            for (int i = 0; i < ranges.Count; i++)
            {
                starts[i] = ranges[i].start;
                ends[i] = ranges[i].end;
                offsets[i] = count;
                count += ranges[i].end - ranges[i].start + 1;
            }

            Count = count;
        }

        public string Pattern { get; }

        public int Count { get; }

        public int RangeCount => starts.Length;

        public static GlyphCharacterSet Parse(string pattern)
        {
            if (pattern == null)
            {
                throw new ArgumentNullException(nameof(pattern));
            }

            int last = pattern.Length - 1;
            if (pattern.Length < 2 || pattern[0] != '[' || pattern[last] != ']')
            {
                throw new FormatException($"'{pattern}' must be one bracketed character class, such as [a-zA-Z0-9].");
            }

            int i = 1;
            if (i < last && pattern[i] == '^')
            {
                throw new FormatException($"'{pattern}' is negated; list the characters to draw instead.");
            }

            if (i == last)
            {
                throw new FormatException($"'{pattern}' is empty.");
            }

            var ranges = new List<(int start, int end)>();
            while (i < last)
            {
                int first = ReadCharacter(pattern, ref i, last);

                // A dash just before the closing bracket is a literal, read on the next pass.
                if (i < last - 1 && pattern[i] == '-')
                {
                    i++;
                    int end = ReadCharacter(pattern, ref i, last);
                    if (end < first)
                    {
                        throw new FormatException($"'{pattern}' has the reversed range {Describe(first)}-{Describe(end)}.");
                    }

                    ranges.Add((first, end));
                }
                else
                {
                    ranges.Add((first, first));
                }
            }

            ranges.Sort((a, b) => a.start.CompareTo(b.start));
            var merged = new List<(int start, int end)>(ranges.Count);
            foreach ((int start, int end) range in ranges)
            {
                if (merged.Count > 0 && range.start <= merged[merged.Count - 1].end + 1)
                {
                    (int start, int end) previous = merged[merged.Count - 1];
                    merged[merged.Count - 1] = (previous.start, Math.Max(previous.end, range.end));
                }
                else
                {
                    merged.Add(range);
                }
            }

            return new GlyphCharacterSet(pattern, merged);
        }

        public int CodepointAt(int index)
        {
            if (index < 0 || index >= Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, $"The set has {Count} characters.");
            }

            int range = Array.BinarySearch(offsets, index);
            if (range < 0)
            {
                range = ~range - 1;
            }

            return starts[range] + index - offsets[range];
        }

        public bool Contains(int codepoint)
        {
            int range = Array.BinarySearch(starts, codepoint);
            if (range >= 0)
            {
                return true;
            }

            range = ~range - 1;
            return range >= 0 && codepoint <= ends[range];
        }

        /// <summary>
        /// The character a seed selects. Every caller with the same seed gets the same
        /// character, which is what lets clients agree without being told.
        /// </summary>
        public int Pick(ulong seed) => CodepointAt((int)(Mix(seed) % (ulong)Count));

        /// <summary>
        /// The splitmix64 output for a generator in state <paramref name="seed"/>: scatters
        /// neighbouring seeds, such as consecutive net ids, across the whole range.
        /// </summary>
        public static ulong Mix(ulong seed)
        {
            ulong z = seed + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public IEnumerable<int> EnumerateCodepoints()
        {
            for (int range = 0; range < starts.Length; range++)
            {
                for (int codepoint = starts[range]; codepoint <= ends[range]; codepoint++)
                {
                    yield return codepoint;
                }
            }
        }

        public override string ToString() => $"{Pattern} ({Count} characters)";

        private static int ReadCharacter(string pattern, ref int i, int last)
        {
            char c = pattern[i];
            if (c == '\\')
            {
                if (i + 1 >= last)
                {
                    throw new FormatException($"'{pattern}' ends with a lone backslash.");
                }

                char escaped = pattern[i + 1];
                if (escaped == 'u')
                {
                    if (i + 6 > last ||
                        !int.TryParse(pattern.Substring(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int value))
                    {
                        throw new FormatException($"'{pattern}' has a \\u escape without four hex digits.");
                    }

                    if (value >= 0xD800 && value <= 0xDFFF)
                    {
                        throw new FormatException($"'{pattern}' escapes the surrogate U+{value:X4}; write the character itself.");
                    }

                    i += 6;
                    return value;
                }

                if ("\\[]-^".IndexOf(escaped) < 0)
                {
                    throw new FormatException($"'{pattern}' has the unsupported escape \\{escaped}.");
                }

                i += 2;
                return escaped;
            }

            if (c == '[' || c == ']')
            {
                throw new FormatException($"'{pattern}' has an unescaped '{c}' inside the class.");
            }

            if (char.IsHighSurrogate(c) && i + 1 < last && char.IsLowSurrogate(pattern[i + 1]))
            {
                int codepoint = char.ConvertToUtf32(c, pattern[i + 1]);
                i += 2;
                return codepoint;
            }

            if (char.IsSurrogate(c))
            {
                throw new FormatException($"'{pattern}' has an unpaired surrogate.");
            }

            i++;
            return c;
        }

        private static string Describe(int codepoint) => WaxGlyphAssets.Describe(codepoint);
    }
}
