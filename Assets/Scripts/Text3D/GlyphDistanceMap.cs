using System;
using System.Collections.Generic;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>
    /// Bakes the edge distance map WaxCandy V7 reads from <c>_EdgeDistanceMap</c>.
    /// </summary>
    /// <remarks>
    /// Texels inside the glyph store their distance to the nearest outline edge divided
    /// by the distance range; texels outside store zero, as in the reference letter's map.
    ///
    /// Distances are dead reckoned: texels next to an edge measure it directly, then two
    /// sweeps hand each texel the nearest edge its neighbours found and it measures that
    /// edge itself. Every stored value is therefore a true distance to a real edge, which
    /// keeps the gradient the puff lighting follows free of the axis-aligned bias of
    /// grid-only distance transforms, while the cost stays linear in texels.
    ///
    /// The sweeps only visit texels inside the glyph. The straight line from an inside
    /// point to its nearest edge never leaves the glyph, so the nearest edge always
    /// reaches it through inside neighbours, and outside texels would only be written
    /// back to zero.
    ///
    /// The texel loops run Burst-compiled in <see cref="GlyphDistanceMapBurst"/>; finding
    /// the inside spans stays managed because it sorts. The per-texel scratch arrays are
    /// kept per thread: a CJK glyph needs several megabytes of them, and allocating that
    /// much for every glyph would trigger collections that pause the main thread too.
    ///
    /// Values are 16-bit. V7 notes that 8-bit steps show up in the puff; the asset step
    /// falls back to 8 bits where R16 textures are unsupported.
    /// </remarks>
    public static class GlyphDistanceMap
    {
        [ThreadStatic]
        private static int[] nearestEdgeBuffer;

        [ThreadStatic]
        private static double[] nearestSquaredBuffer;

        /// <summary>Whether the texel loops run Burst-compiled rather than as plain C#.</summary>
        public static unsafe bool UsesBurst
        {
            get
            {
                byte managed = 1;
                GlyphDistanceMapBurst.Probe(&managed);
                return managed == 0;
            }
        }

        /// <param name="loops">Filled outline, outer loops and holes, with no overlaps.</param>
        /// <param name="texelSize">Object units per texel; texel (0, 0) starts at <paramref name="rectX"/>, <paramref name="rectY"/>.</param>
        /// <param name="maxDistance">Largest distance found inside the outline, in object units.</param>
        /// <returns>Rows bottom to top, matching Unity texture data and planar UVs.</returns>
        public static unsafe ushort[] Bake(
            IReadOnlyList<IReadOnlyList<GlyphVector>> loops,
            double rectX,
            double rectY,
            int width,
            int height,
            double texelSize,
            double distanceRange,
            out double maxDistance)
        {
            var map = new ushort[width * height];
            maxDistance = 0.0;

            int edgeCount = 0;
            foreach (IReadOnlyList<GlyphVector> loop in loops)
            {
                edgeCount += loop.Count;
            }

            if (edgeCount == 0 || distanceRange <= 0.0)
            {
                return map;
            }

            // Edges in texel space, with texel centers on integers.
            double texelsPerUnit = 1.0 / texelSize;
            var edgeAX = new double[edgeCount];
            var edgeAY = new double[edgeCount];
            var edgeBX = new double[edgeCount];
            var edgeBY = new double[edgeCount];
            int edge = 0;
            foreach (IReadOnlyList<GlyphVector> loop in loops)
            {
                for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++, edge++)
                {
                    edgeAX[edge] = (loop[j].X - rectX) * texelsPerUnit - 0.5;
                    edgeAY[edge] = (loop[j].Y - rectY) * texelsPerUnit - 0.5;
                    edgeBX[edge] = (loop[i].X - rectX) * texelsPerUnit - 0.5;
                    edgeBY[edge] = (loop[i].Y - rectY) * texelsPerUnit - 0.5;
                }
            }

            int[] spans = InsideSpans(edgeAX, edgeAY, edgeBX, edgeBY, width, height, out int[] rowSpanStarts);

            int texelCount = width * height;
            int[] nearestEdge = nearestEdgeBuffer;
            if (nearestEdge == null || nearestEdge.Length < texelCount)
            {
                nearestEdgeBuffer = nearestEdge = new int[texelCount];
            }

            double[] nearestSquared = nearestSquaredBuffer;
            if (nearestSquared == null || nearestSquared.Length < texelCount)
            {
                nearestSquaredBuffer = nearestSquared = new double[texelCount];
            }

            double maxSquared = 0.0;
            fixed (double* ax = edgeAX, ay = edgeAY, bx = edgeBX, by = edgeBY, squared = nearestSquared)
            fixed (int* spanData = spans, rowStarts = rowSpanStarts, nearest = nearestEdge)
            fixed (ushort* mapData = map)
            {
                GlyphDistanceMapBurst.Solve(
                    ax,
                    ay,
                    bx,
                    by,
                    edgeCount,
                    spanData,
                    rowStarts,
                    width,
                    height,
                    nearest,
                    squared,
                    mapData,
                    texelSize / distanceRange * ushort.MaxValue,
                    &maxSquared);
            }

            maxDistance = Math.Min(Math.Sqrt(maxSquared) * texelSize, distanceRange);
            return map;
        }

        // Inside texels as inclusive [first, last] column pairs per row, by even-odd crossings at texel centers.
        private static int[] InsideSpans(
            double[] ax,
            double[] ay,
            double[] bx,
            double[] by,
            int width,
            int height,
            out int[] rowSpanStarts)
        {
            var spans = new List<int>();
            rowSpanStarts = new int[height + 1];
            var crossings = new List<double>();
            for (int y = 0; y < height; y++)
            {
                rowSpanStarts[y] = spans.Count;
                crossings.Clear();
                for (int e = 0; e < ax.Length; e++)
                {
                    if ((ay[e] <= y) != (by[e] <= y))
                    {
                        crossings.Add(ax[e] + (y - ay[e]) * (bx[e] - ax[e]) / (by[e] - ay[e]));
                    }
                }

                crossings.Sort();
                for (int k = 0; k + 1 < crossings.Count; k += 2)
                {
                    int first = Math.Max(0, (int)Math.Ceiling(crossings[k]));
                    int last = Math.Min(width - 1, (int)Math.Ceiling(crossings[k + 1]) - 1);
                    if (first <= last)
                    {
                        spans.Add(first);
                        spans.Add(last);
                    }
                }
            }

            rowSpanStarts[height] = spans.Count;
            return spans.ToArray();
        }
    }
}
