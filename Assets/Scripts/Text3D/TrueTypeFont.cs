using System;
using System.Collections.Generic;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>One TrueType outline point, in font units.</summary>
    public readonly struct GlyphOutlinePoint
    {
        public readonly double X;
        public readonly double Y;

        /// <summary>False for a quadratic control point.</summary>
        public readonly bool OnCurve;

        public GlyphOutlinePoint(double x, double y, bool onCurve)
        {
            X = x;
            Y = y;
            OnCurve = onCurve;
        }
    }

    /// <summary>
    /// Reads the character map, horizontal metrics and quadratic outlines of a TrueType font.
    /// </summary>
    /// <remarks>
    /// Only the tables a glyph mesh needs are located, and glyph data is decoded on
    /// demand, so opening a 7 MB CJK font costs a walk of the table directory rather
    /// than a full parse. Nothing is written after construction, which lets any number
    /// of build threads share one instance.
    ///
    /// Not read: CFF outlines ("OTTO" fonts), font collections, variable-font deltas,
    /// hinting, and kerning or shaping tables.
    /// </remarks>
    public sealed class TrueTypeFont
    {
        private const uint TrueTypeVersion = 0x00010000;
        private const uint AppleTrueTypeVersion = 0x74727565; // 'true'
        private const uint OpenTypeCffVersion = 0x4F54544F; // 'OTTO'
        private const uint CollectionVersion = 0x74746366; // 'ttcf'

        private const uint HeadTag = 0x68656164;
        private const uint MaxpTag = 0x6D617870;
        private const uint CmapTag = 0x636D6170;
        private const uint LocaTag = 0x6C6F6361;
        private const uint GlyfTag = 0x676C7966;
        private const uint HheaTag = 0x68686561;
        private const uint HmtxTag = 0x686D7478;
        private const uint Os2Tag = 0x4F532F32;

        private const int ArgumentsAreWords = 0x0001;
        private const int ArgumentsAreXYValues = 0x0002;
        private const int HasScale = 0x0008;
        private const int MoreComponents = 0x0020;
        private const int HasXYScale = 0x0040;
        private const int HasTwoByTwo = 0x0080;
        private const int MaxCompositeDepth = 8;

        private readonly byte[] data;
        private readonly int glyfOffset;
        private readonly int glyfLength;
        private readonly int locaOffset;
        private readonly bool longLocaOffsets;
        private readonly int hmtxOffset;
        private readonly int horizontalMetricCount;
        private readonly int segmentMapOffset = -1;
        private readonly int segmentedCoverageOffset = -1;

        private TrueTypeFont(byte[] data)
        {
            this.data = data;
            if (data.Length < 12)
            {
                throw new FormatException("Font data is too short to hold a table directory.");
            }

            uint version = ReadUInt32(0);
            if (version == OpenTypeCffVersion)
            {
                throw new NotSupportedException(
                    "CFF-based OpenType fonts (OTTO) are not supported; use a TrueType font with glyf outlines.");
            }

            if (version == CollectionVersion)
            {
                throw new NotSupportedException("TrueType collections (.ttc) are not supported.");
            }

            if (version != TrueTypeVersion && version != AppleTrueTypeVersion)
            {
                throw new FormatException($"Unknown sfnt version 0x{version:X8}.");
            }

            int head = -1;
            int maxp = -1;
            int cmap = -1;
            int loca = -1;
            int glyf = -1;
            int hhea = -1;
            int hmtx = -1;
            int os2 = -1;
            long os2Length = 0;
            int tableCount = ReadUInt16(4);
            for (int i = 0; i < tableCount; i++)
            {
                int record = 12 + 16 * i;
                long offset = ReadUInt32(record + 8);
                long length = ReadUInt32(record + 12);
                if (offset + length > data.Length)
                {
                    throw new FormatException("A font table lies outside the file.");
                }

                switch (ReadUInt32(record))
                {
                    case HeadTag: head = (int)offset; break;
                    case MaxpTag: maxp = (int)offset; break;
                    case CmapTag: cmap = (int)offset; break;
                    case LocaTag: loca = (int)offset; break;
                    case GlyfTag: glyf = (int)offset; glyfLength = (int)length; break;
                    case HheaTag: hhea = (int)offset; break;
                    case HmtxTag: hmtx = (int)offset; break;
                    case Os2Tag: os2 = (int)offset; os2Length = length; break;
                }
            }

            if (head < 0 || maxp < 0 || cmap < 0 || hhea < 0 || hmtx < 0)
            {
                throw new FormatException("Font is missing a required table (head, maxp, cmap, hhea or hmtx).");
            }

            if (loca < 0 || glyf < 0)
            {
                throw new NotSupportedException("Font has no glyf outlines; only TrueType outlines are supported.");
            }

            glyfOffset = glyf;
            locaOffset = loca;
            hmtxOffset = hmtx;
            UnitsPerEm = ReadUInt16(head + 18);
            longLocaOffsets = ReadInt16(head + 50) != 0;
            GlyphCount = ReadUInt16(maxp + 4);
            Ascender = ReadInt16(hhea + 4);
            Descender = ReadInt16(hhea + 6);
            horizontalMetricCount = Math.Max(1, ReadUInt16(hhea + 34));

            int subtableCount = ReadUInt16(cmap + 2);
            for (int i = 0; i < subtableCount; i++)
            {
                int record = cmap + 4 + 8 * i;
                int platform = ReadUInt16(record);
                int encoding = ReadUInt16(record + 2);
                bool unicode = platform == 0 || (platform == 3 && (encoding == 1 || encoding == 10));
                if (!unicode)
                {
                    continue;
                }

                int subtable = cmap + (int)ReadUInt32(record + 4);
                int format = ReadUInt16(subtable);
                if (format == 12 && segmentedCoverageOffset < 0)
                {
                    segmentedCoverageOffset = subtable;
                }
                else if (format == 4 && segmentMapOffset < 0)
                {
                    segmentMapOffset = subtable;
                }
            }

            if (segmentMapOffset < 0 && segmentedCoverageOffset < 0)
            {
                throw new NotSupportedException("Font has no Unicode character map (cmap format 4 or 12).");
            }

            int capHeight = os2 >= 0 && os2Length >= 90 && ReadUInt16(os2) >= 2 ? ReadInt16(os2 + 88) : 0;
            if (capHeight <= 0)
            {
                capHeight = GlyphTop(GetGlyphIndex('H'));
            }

            CapHeight = capHeight > 0 ? capHeight : (int)Math.Round(UnitsPerEm * 0.7);
        }

        public int UnitsPerEm { get; }

        public int GlyphCount { get; }

        public int Ascender { get; }

        public int Descender { get; }

        /// <summary>OS/2 capital height, or the top of "H" when the font does not declare one.</summary>
        public int CapHeight { get; }

        /// <exception cref="FormatException">The data is not a readable TrueType font.</exception>
        /// <exception cref="NotSupportedException">The font is valid but uses CFF outlines or is a collection.</exception>
        public static TrueTypeFont Load(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            try
            {
                return new TrueTypeFont(data);
            }
            catch (IndexOutOfRangeException exception)
            {
                throw new FormatException("Font data is truncated.", exception);
            }
        }

        /// <summary>Glyph for a Unicode code point, or 0 (the missing glyph) when the font has none.</summary>
        public int GetGlyphIndex(int codepoint)
        {
            if (codepoint < 0)
            {
                return 0;
            }

            if (segmentedCoverageOffset >= 0)
            {
                long groupCount = ReadUInt32(segmentedCoverageOffset + 12);
                long low = 0;
                long high = groupCount - 1;
                while (low <= high)
                {
                    long middle = (low + high) / 2;
                    int group = segmentedCoverageOffset + 16 + 12 * (int)middle;
                    uint startCode = ReadUInt32(group);
                    uint endCode = ReadUInt32(group + 4);
                    if ((uint)codepoint < startCode)
                    {
                        high = middle - 1;
                    }
                    else if ((uint)codepoint > endCode)
                    {
                        low = middle + 1;
                    }
                    else
                    {
                        long glyph = ReadUInt32(group + 8) + ((uint)codepoint - startCode);
                        return glyph < GlyphCount ? (int)glyph : 0;
                    }
                }

                return 0;
            }

            if (codepoint > 0xFFFF)
            {
                return 0;
            }

            int segmentCountTimesTwo = ReadUInt16(segmentMapOffset + 6);
            int segmentCount = segmentCountTimesTwo / 2;
            int endCodes = segmentMapOffset + 14;
            int startCodes = endCodes + segmentCountTimesTwo + 2;
            int idDeltas = startCodes + segmentCountTimesTwo;
            int idRangeOffsets = idDeltas + segmentCountTimesTwo;

            int first = 0;
            int last = segmentCount - 1;
            while (first <= last)
            {
                int middle = (first + last) / 2;
                if (ReadUInt16(endCodes + 2 * middle) < codepoint)
                {
                    first = middle + 1;
                }
                else
                {
                    last = middle - 1;
                }
            }

            if (first >= segmentCount)
            {
                return 0;
            }

            int segmentStart = ReadUInt16(startCodes + 2 * first);
            if (codepoint < segmentStart)
            {
                return 0;
            }

            int delta = ReadUInt16(idDeltas + 2 * first);
            int rangeOffsetPosition = idRangeOffsets + 2 * first;
            int rangeOffset = ReadUInt16(rangeOffsetPosition);
            int glyphIndex;
            if (rangeOffset == 0)
            {
                glyphIndex = (codepoint + delta) & 0xFFFF;
            }
            else
            {
                int raw = ReadUInt16(rangeOffsetPosition + rangeOffset + 2 * (codepoint - segmentStart));
                glyphIndex = raw == 0 ? 0 : (raw + delta) & 0xFFFF;
            }

            return glyphIndex < GlyphCount ? glyphIndex : 0;
        }

        /// <summary>Horizontal advance in font units.</summary>
        public int GetAdvanceWidth(int glyphIndex)
        {
            int metric = Math.Min(Math.Max(glyphIndex, 0), horizontalMetricCount - 1);
            return ReadUInt16(hmtxOffset + 4 * metric);
        }

        /// <summary>
        /// Appends the glyph's contours in font units. Composite glyphs are expanded with
        /// each component's transform applied. Empty glyphs, such as a space, add nothing.
        /// </summary>
        public void AppendOutline(int glyphIndex, List<List<GlyphOutlinePoint>> contours)
        {
            if (contours == null)
            {
                throw new ArgumentNullException(nameof(contours));
            }

            try
            {
                AppendOutline(glyphIndex, contours, 1.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0);
            }
            catch (IndexOutOfRangeException exception)
            {
                throw new FormatException($"Glyph {glyphIndex} data is truncated.", exception);
            }
        }

        // Places each point at (xx * x + yx * y + dx, xy * x + yy * y + dy).
        private void AppendOutline(
            int glyphIndex,
            List<List<GlyphOutlinePoint>> contours,
            double xx,
            double xy,
            double yx,
            double yy,
            double dx,
            double dy,
            int depth)
        {
            if (depth > MaxCompositeDepth || !TryGetGlyphRange(glyphIndex, out int start, out int length) || length < 10)
            {
                return;
            }

            int contourCount = ReadInt16(start);
            if (contourCount >= 0)
            {
                AppendSimpleOutline(start, contourCount, contours, xx, xy, yx, yy, dx, dy);
                return;
            }

            int position = start + 10;
            int flags;
            do
            {
                flags = ReadUInt16(position);
                int componentGlyph = ReadUInt16(position + 2);
                position += 4;

                double argument1;
                double argument2;
                if ((flags & ArgumentsAreWords) != 0)
                {
                    argument1 = ReadInt16(position);
                    argument2 = ReadInt16(position + 2);
                    position += 4;
                }
                else
                {
                    argument1 = (sbyte)data[position];
                    argument2 = (sbyte)data[position + 1];
                    position += 2;
                }

                // Component point = (a * x + c * y + e, b * x + d * y + f).
                double a = 1.0;
                double b = 0.0;
                double c = 0.0;
                double d = 1.0;
                if ((flags & HasScale) != 0)
                {
                    a = d = ReadF2Dot14(position);
                    position += 2;
                }
                else if ((flags & HasXYScale) != 0)
                {
                    a = ReadF2Dot14(position);
                    d = ReadF2Dot14(position + 2);
                    position += 4;
                }
                else if ((flags & HasTwoByTwo) != 0)
                {
                    a = ReadF2Dot14(position);
                    b = ReadF2Dot14(position + 2);
                    c = ReadF2Dot14(position + 4);
                    d = ReadF2Dot14(position + 6);
                    position += 8;
                }

                // Placement by matching point numbers is rare in Latin and CJK fonts; such
                // components are left at the origin instead.
                bool offsetPlacement = (flags & ArgumentsAreXYValues) != 0;
                double e = offsetPlacement ? argument1 : 0.0;
                double f = offsetPlacement ? argument2 : 0.0;

                AppendOutline(
                    componentGlyph,
                    contours,
                    xx * a + yx * b,
                    xy * a + yy * b,
                    xx * c + yx * d,
                    xy * c + yy * d,
                    xx * e + yx * f + dx,
                    xy * e + yy * f + dy,
                    depth + 1);
            }
            while ((flags & MoreComponents) != 0);
        }

        private void AppendSimpleOutline(
            int start,
            int contourCount,
            List<List<GlyphOutlinePoint>> contours,
            double xx,
            double xy,
            double yx,
            double yy,
            double dx,
            double dy)
        {
            if (contourCount == 0)
            {
                return;
            }

            int endPoints = start + 10;
            int pointCount = ReadUInt16(endPoints + 2 * (contourCount - 1)) + 1;
            int instructionLength = ReadUInt16(endPoints + 2 * contourCount);
            int position = endPoints + 2 * contourCount + 2 + instructionLength;

            var flags = new byte[pointCount];
            for (int i = 0; i < pointCount;)
            {
                byte flag = data[position++];
                flags[i++] = flag;
                if ((flag & 0x08) != 0)
                {
                    int repeat = data[position++];
                    for (int r = 0; r < repeat && i < pointCount; r++)
                    {
                        flags[i++] = flag;
                    }
                }
            }

            var xs = new int[pointCount];
            int value = 0;
            for (int i = 0; i < pointCount; i++)
            {
                byte flag = flags[i];
                if ((flag & 0x02) != 0)
                {
                    int delta = data[position++];
                    value += (flag & 0x10) != 0 ? delta : -delta;
                }
                else if ((flag & 0x10) == 0)
                {
                    value += ReadInt16(position);
                    position += 2;
                }

                xs[i] = value;
            }

            var ys = new int[pointCount];
            value = 0;
            for (int i = 0; i < pointCount; i++)
            {
                byte flag = flags[i];
                if ((flag & 0x04) != 0)
                {
                    int delta = data[position++];
                    value += (flag & 0x20) != 0 ? delta : -delta;
                }
                else if ((flag & 0x20) == 0)
                {
                    value += ReadInt16(position);
                    position += 2;
                }

                ys[i] = value;
            }

            int first = 0;
            for (int contour = 0; contour < contourCount; contour++)
            {
                int last = ReadUInt16(endPoints + 2 * contour);
                if (last < first || last >= pointCount)
                {
                    break;
                }

                var points = new List<GlyphOutlinePoint>(last - first + 1);
                for (int i = first; i <= last; i++)
                {
                    points.Add(new GlyphOutlinePoint(
                        xx * xs[i] + yx * ys[i] + dx,
                        xy * xs[i] + yy * ys[i] + dy,
                        (flags[i] & 0x01) != 0));
                }

                contours.Add(points);
                first = last + 1;
            }
        }

        private bool TryGetGlyphRange(int glyphIndex, out int start, out int length)
        {
            start = 0;
            length = 0;
            if (glyphIndex < 0 || glyphIndex >= GlyphCount)
            {
                return false;
            }

            long begin;
            long end;
            if (longLocaOffsets)
            {
                begin = ReadUInt32(locaOffset + 4 * glyphIndex);
                end = ReadUInt32(locaOffset + 4 * (glyphIndex + 1));
            }
            else
            {
                begin = ReadUInt16(locaOffset + 2 * glyphIndex) * 2L;
                end = ReadUInt16(locaOffset + 2 * (glyphIndex + 1)) * 2L;
            }

            if (end <= begin || end > glyfLength)
            {
                return false;
            }

            start = glyfOffset + (int)begin;
            length = (int)(end - begin);
            return true;
        }

        private int GlyphTop(int glyphIndex) =>
            TryGetGlyphRange(glyphIndex, out int start, out int length) && length >= 10 ? ReadInt16(start + 8) : 0;

        private int ReadUInt16(int offset) => (data[offset] << 8) | data[offset + 1];

        private int ReadInt16(int offset) => (short)((data[offset] << 8) | data[offset + 1]);

        private uint ReadUInt32(int offset) =>
            (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

        private double ReadF2Dot14(int offset) => ReadInt16(offset) / 16384.0;
    }
}
