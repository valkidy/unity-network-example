using System;
using System.Collections.Generic;
using System.Text;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>Where one <see cref="WaxGlyphText"/> build spent its time, and what it made.</summary>
    public sealed class WaxGlyphBuildReport
    {
        private readonly List<WaxGlyphGeometry> glyphs = new List<WaxGlyphGeometry>();

        internal WaxGlyphBuildReport(string text)
        {
            Text = text;
        }

        public string Text { get; }

        public int MainThreadId { get; internal set; }

        public int BackgroundThreadId { get; internal set; }

        public double FontLoadMilliseconds { get; internal set; }

        /// <summary>Font loading plus every glyph build, all on the background thread.</summary>
        public double BackgroundMilliseconds { get; internal set; }

        /// <summary>Longest stretch of main-thread asset creation between frames.</summary>
        public double MaxMainThreadSliceMilliseconds { get; private set; }

        /// <summary>Frames the main-thread asset creation was spread over.</summary>
        public int MainThreadSlices { get; private set; }

        public int MissingCharacters { get; internal set; }

        /// <summary>Distinct characters with an outline, in order of first use.</summary>
        public IReadOnlyList<WaxGlyphGeometry> Glyphs => glyphs;

        public int MaxTrianglesPerGlyph
        {
            get
            {
                int max = 0;
                foreach (WaxGlyphGeometry glyph in glyphs)
                {
                    max = Math.Max(max, glyph.TriangleCount);
                }

                return max;
            }
        }

        internal void AddGlyph(WaxGlyphGeometry glyph) => glyphs.Add(glyph);

        internal void RecordMainThreadSlice(double milliseconds)
        {
            MainThreadSlices++;
            MaxMainThreadSliceMilliseconds = Math.Max(MaxMainThreadSliceMilliseconds, milliseconds);
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.AppendLine(
                $"WaxGlyphText \"{Text}\": background {BackgroundMilliseconds:0.0} ms (font {FontLoadMilliseconds:0.0} ms, " +
                $"thread {BackgroundThreadId}), main thread {MainThreadSlices} slice(s), longest {MaxMainThreadSliceMilliseconds:0.00} ms " +
                $"(thread {MainThreadId}), missing {MissingCharacters}");
            foreach (WaxGlyphGeometry glyph in glyphs)
            {
                builder.AppendLine(
                    $"  {WaxGlyphAssets.Describe(glyph.Codepoint)} tris {glyph.TriangleCount} rings {glyph.WallSegments + 1} " +
                    $"outline {glyph.OutlineVertexCount} map {glyph.MapWidth}x{glyph.MapHeight} " +
                    $"half-stroke {glyph.MaxInteriorDistance:0.000}{(glyph.Simplified ? " simplified" : string.Empty)}" +
                    $"{(glyph.TriangulationClean ? string.Empty : " FORCED-TRIANGLES")} | outline {glyph.OutlineMilliseconds:0.00} " +
                    $"union {glyph.UnionMilliseconds:0.00} fit {glyph.SimplifyMilliseconds:0.00} mesh {glyph.MeshMilliseconds:0.00} " +
                    $"map {glyph.MapMilliseconds:0.00} ms");
            }

            return builder.ToString();
        }
    }
}
