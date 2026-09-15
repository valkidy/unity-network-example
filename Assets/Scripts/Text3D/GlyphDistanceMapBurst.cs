using System;
using Unity.Burst;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>
    /// Burst-compiled texel loops of <see cref="GlyphDistanceMap"/>.
    /// </summary>
    /// <remarks>
    /// Invoked as a Burst direct call from whichever thread builds the glyph, on managed
    /// arrays the caller pins, so no job has to be scheduled from the main thread. Where
    /// Burst is disabled, or in the Editor before compilation finishes, the same code runs
    /// as plain C#; <see cref="Probe"/> tells the two apart.
    ///
    /// Compilation is deliberately left asynchronous. Compiling synchronously made the
    /// Editor's first bake after a script reload wait several seconds for Burst, which
    /// delayed the first text that long; players are compiled ahead of time either way.
    ///
    /// Edges are in texel space with texel centers on integers. Spans list the inside
    /// texels of each row as inclusive [first, last] column pairs, and row y's pairs are
    /// <c>spans[rowSpanStarts[y]]</c> up to <c>spans[rowSpanStarts[y + 1]]</c>.
    /// </remarks>
    [BurstCompile]
    internal static unsafe class GlyphDistanceMapBurst
    {
        [BurstCompile]
        public static void Solve(
            double* ax,
            double* ay,
            double* bx,
            double* by,
            int edgeCount,
            int* spans,
            int* rowSpanStarts,
            int width,
            int height,
            int* nearestEdge,
            double* nearestSquared,
            ushort* map,
            double valueScale,
            double* maxSquared)
        {
            int texelCount = width * height;
            for (int i = 0; i < texelCount; i++)
            {
                nearestEdge[i] = -1;
                nearestSquared[i] = double.PositiveInfinity;
            }

            // Measure every texel within a texel of each edge against that edge: walk the
            // edge in one-texel steps and measure the 4x4 texels around each step.
            for (int e = 0; e < edgeCount; e++)
            {
                double dx = bx[e] - ax[e];
                double dy = by[e] - ay[e];
                int samples = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy)));
                for (int s = 0; s <= samples; s++)
                {
                    double t = (double)s / samples;
                    int column = (int)Math.Floor(ax[e] + dx * t);
                    int row = (int)Math.Floor(ay[e] + dy * t);
                    int rowEnd = Math.Min(height - 1, row + 2);
                    int columnEnd = Math.Min(width - 1, column + 2);
                    for (int y = Math.Max(0, row - 1); y <= rowEnd; y++)
                    {
                        for (int x = Math.Max(0, column - 1); x <= columnEnd; x++)
                        {
                            int index = y * width + x;
                            if (nearestEdge[index] == e)
                            {
                                continue;
                            }

                            double squared = DistanceSquared(ax, ay, bx, by, e, x, y);
                            if (squared < nearestSquared[index])
                            {
                                nearestSquared[index] = squared;
                                nearestEdge[index] = e;
                            }
                        }
                    }
                }
            }

            for (int y = 0; y < height; y++)
            {
                for (int s = rowSpanStarts[y]; s < rowSpanStarts[y + 1]; s += 2)
                {
                    for (int x = spans[s]; x <= spans[s + 1]; x++)
                    {
                        int index = y * width + x;
                        if (x > 0)
                        {
                            Adopt(index, index - 1, x, y, ax, ay, bx, by, nearestEdge, nearestSquared);
                        }

                        if (y > 0)
                        {
                            int below = index - width;
                            if (x > 0)
                            {
                                Adopt(index, below - 1, x, y, ax, ay, bx, by, nearestEdge, nearestSquared);
                            }

                            Adopt(index, below, x, y, ax, ay, bx, by, nearestEdge, nearestSquared);
                            if (x + 1 < width)
                            {
                                Adopt(index, below + 1, x, y, ax, ay, bx, by, nearestEdge, nearestSquared);
                            }
                        }
                    }
                }
            }

            for (int y = height - 1; y >= 0; y--)
            {
                for (int s = rowSpanStarts[y + 1] - 2; s >= rowSpanStarts[y]; s -= 2)
                {
                    for (int x = spans[s + 1]; x >= spans[s]; x--)
                    {
                        int index = y * width + x;
                        if (x + 1 < width)
                        {
                            Adopt(index, index + 1, x, y, ax, ay, bx, by, nearestEdge, nearestSquared);
                        }

                        if (y + 1 < height)
                        {
                            int above = index + width;
                            if (x + 1 < width)
                            {
                                Adopt(index, above + 1, x, y, ax, ay, bx, by, nearestEdge, nearestSquared);
                            }

                            Adopt(index, above, x, y, ax, ay, bx, by, nearestEdge, nearestSquared);
                            if (x > 0)
                            {
                                Adopt(index, above - 1, x, y, ax, ay, bx, by, nearestEdge, nearestSquared);
                            }
                        }
                    }
                }
            }

            double largest = 0.0;
            for (int y = 0; y < height; y++)
            {
                for (int s = rowSpanStarts[y]; s < rowSpanStarts[y + 1]; s += 2)
                {
                    for (int x = spans[s]; x <= spans[s + 1]; x++)
                    {
                        int index = y * width + x;
                        double squared = nearestSquared[index];
                        largest = Math.Max(largest, squared);
                        map[index] = (ushort)Math.Min(ushort.MaxValue, Math.Round(Math.Sqrt(squared) * valueScale));
                    }
                }
            }

            *maxSquared = largest;
        }

        /// <summary>Sets <paramref name="managed"/> to 0 when Burst runs this code and 1 when plain C# does.</summary>
        [BurstCompile]
        public static void Probe(byte* managed)
        {
            *managed = 0;
            MarkManaged(managed);
        }

        [BurstDiscard]
        private static void MarkManaged(byte* managed)
        {
            *managed = 1;
        }

        private static void Adopt(
            int index,
            int neighbour,
            int x,
            int y,
            double* ax,
            double* ay,
            double* bx,
            double* by,
            int* nearestEdge,
            double* nearestSquared)
        {
            int edge = nearestEdge[neighbour];
            if (edge < 0 || edge == nearestEdge[index])
            {
                return;
            }

            double squared = DistanceSquared(ax, ay, bx, by, edge, x, y);
            if (squared < nearestSquared[index])
            {
                nearestSquared[index] = squared;
                nearestEdge[index] = edge;
            }
        }

        private static double DistanceSquared(double* ax, double* ay, double* bx, double* by, int edge, double px, double py)
        {
            double abx = bx[edge] - ax[edge];
            double aby = by[edge] - ay[edge];
            double apx = px - ax[edge];
            double apy = py - ay[edge];
            double lengthSquared = abx * abx + aby * aby;
            double t = lengthSquared > 0.0 ? (apx * abx + apy * aby) / lengthSquared : 0.0;
            if (t < 0.0)
            {
                t = 0.0;
            }
            else if (t > 1.0)
            {
                t = 1.0;
            }

            double offsetX = apx - abx * t;
            double offsetY = apy - aby * t;
            return offsetX * offsetX + offsetY * offsetY;
        }
    }
}
