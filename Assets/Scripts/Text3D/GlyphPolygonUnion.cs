using System;
using System.Collections.Generic;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>
    /// Merges a glyph's contours into the boundary of their non-zero fill.
    /// </summary>
    /// <remarks>
    /// Fonts may draw a letter from overlapping pieces -- Noto Sans TC Bold builds "A",
    /// "Q", "R" and "f" that way. Extruding those contours directly would put walls
    /// inside the solid and give the edge distance map edges that are not on the
    /// silhouette.
    ///
    /// Every edge is split where it meets another, each piece is kept only when the
    /// fill differs on its two sides, and the kept pieces are chained back into loops.
    /// Pieces are turned so the filled side is on their left, so outer loops come out
    /// counter-clockwise and holes clockwise whatever the font's own orientation was.
    ///
    /// The pairwise split is quadratic in edge count: a few hundred edges for a Latin
    /// letter, a few thousand for a dense CJK character.
    /// </remarks>
    public static class GlyphPolygonUnion
    {
        private const double ParameterEpsilon = 1e-9;

        private readonly struct Edge
        {
            public readonly int From;
            public readonly int To;

            public Edge(int from, int to)
            {
                From = from;
                To = to;
            }
        }

        public static List<List<GlyphVector>> Union(IReadOnlyList<IReadOnlyList<GlyphVector>> contours)
        {
            var result = new List<List<GlyphVector>>();
            if (!GlyphPolygon.TryGetBounds(contours, out GlyphVector min, out GlyphVector max))
            {
                return result;
            }

            double size = Math.Max(max.X - min.X, max.Y - min.Y);
            if (size <= 0.0)
            {
                return result;
            }

            double weldDistance = size * 1e-9;
            double sideOffset = size * 1e-6;

            var starts = new List<GlyphVector>();
            var ends = new List<GlyphVector>();
            foreach (IReadOnlyList<GlyphVector> contour in contours)
            {
                if (contour.Count < 3)
                {
                    continue;
                }

                for (int i = 0; i < contour.Count; i++)
                {
                    GlyphVector a = contour[i];
                    GlyphVector b = contour[(i + 1) % contour.Count];
                    if (GlyphVector.Distance(a, b) > weldDistance)
                    {
                        starts.Add(a);
                        ends.Add(b);
                    }
                }
            }

            List<double>[] splits = FindSplits(starts, ends, weldDistance);

            var welder = new VertexWelder(new GlyphVector(min.X - size, min.Y - size), weldDistance);
            var pieces = new List<Edge>();
            for (int i = 0; i < starts.Count; i++)
            {
                List<double> cuts = splits[i];
                cuts.Sort();
                GlyphVector origin = starts[i];
                GlyphVector direction = ends[i] - origin;
                int previous = welder.Weld(origin);
                double lastCut = 0.0;
                foreach (double cut in cuts)
                {
                    if (cut - lastCut <= ParameterEpsilon)
                    {
                        continue;
                    }

                    int vertex = welder.Weld(origin + direction * cut);
                    if (vertex != previous)
                    {
                        pieces.Add(new Edge(previous, vertex));
                    }

                    previous = vertex;
                    lastCut = cut;
                }

                int end = welder.Weld(ends[i]);
                if (end != previous)
                {
                    pieces.Add(new Edge(previous, end));
                }
            }

            List<GlyphVector> vertices = welder.Vertices;
            var boundary = new List<Edge>();
            var seen = new HashSet<long>();
            foreach (Edge piece in pieces)
            {
                GlyphVector a = vertices[piece.From];
                GlyphVector b = vertices[piece.To];
                GlyphVector middle = GlyphVector.Lerp(a, b, 0.5);
                GlyphVector side = (b - a).Normalized().LeftNormal * sideOffset;
                bool leftFilled = Winding(starts, ends, middle + side) != 0;
                bool rightFilled = Winding(starts, ends, middle - side) != 0;
                if (leftFilled == rightFilled)
                {
                    continue;
                }

                Edge oriented = leftFilled ? piece : new Edge(piece.To, piece.From);
                if (seen.Add(((long)oriented.From << 32) | (uint)oriented.To))
                {
                    boundary.Add(oriented);
                }
            }

            foreach (List<int> chain in ChainLoops(boundary, vertices))
            {
                var loop = new List<GlyphVector>(chain.Count);
                foreach (int vertex in chain)
                {
                    loop.Add(vertices[vertex]);
                }

                List<GlyphVector> cleaned = GlyphPolygon.Clean(loop, weldDistance * 10.0);
                if (cleaned.Count >= 3 && Math.Abs(GlyphPolygon.SignedArea(cleaned)) > size * size * 1e-12)
                {
                    result.Add(cleaned);
                }
            }

            return result;
        }

        private static List<double>[] FindSplits(List<GlyphVector> starts, List<GlyphVector> ends, double weldDistance)
        {
            int count = starts.Count;
            var splits = new List<double>[count];
            for (int i = 0; i < count; i++)
            {
                splits[i] = new List<double>();
            }

            double touchDistance = weldDistance * 16.0;
            for (int i = 0; i < count; i++)
            {
                GlyphVector a = starts[i];
                GlyphVector b = ends[i];
                GlyphVector r = b - a;
                double minX = Math.Min(a.X, b.X) - touchDistance;
                double maxX = Math.Max(a.X, b.X) + touchDistance;
                double minY = Math.Min(a.Y, b.Y) - touchDistance;
                double maxY = Math.Max(a.Y, b.Y) + touchDistance;
                for (int j = i + 1; j < count; j++)
                {
                    GlyphVector c = starts[j];
                    GlyphVector d = ends[j];
                    if (Math.Max(c.X, d.X) < minX || Math.Min(c.X, d.X) > maxX ||
                        Math.Max(c.Y, d.Y) < minY || Math.Min(c.Y, d.Y) > maxY)
                    {
                        continue;
                    }

                    GlyphVector s = d - c;
                    double denominator = GlyphVector.Cross(r, s);
                    if (Math.Abs(denominator) > 1e-12 * r.Length * s.Length)
                    {
                        GlyphVector offset = c - a;
                        double t = GlyphVector.Cross(offset, s) / denominator;
                        double u = GlyphVector.Cross(offset, r) / denominator;
                        if (t >= -ParameterEpsilon && t <= 1.0 + ParameterEpsilon &&
                            u >= -ParameterEpsilon && u <= 1.0 + ParameterEpsilon)
                        {
                            AddSplit(splits[i], t);
                            AddSplit(splits[j], u);
                        }
                    }

                    // Endpoints resting on the other edge: T-junctions and collinear overlaps.
                    AddTouch(splits[i], a, r, c, touchDistance);
                    AddTouch(splits[i], a, r, d, touchDistance);
                    AddTouch(splits[j], c, s, a, touchDistance);
                    AddTouch(splits[j], c, s, b, touchDistance);
                }
            }

            return splits;
        }

        private static void AddSplit(List<double> splits, double t)
        {
            if (t > ParameterEpsilon && t < 1.0 - ParameterEpsilon)
            {
                splits.Add(t);
            }
        }

        private static void AddTouch(List<double> splits, GlyphVector origin, GlyphVector direction, GlyphVector point, double touchDistance)
        {
            double lengthSquared = direction.LengthSquared;
            double t = GlyphVector.Dot(point - origin, direction) / lengthSquared;
            if (t <= ParameterEpsilon || t >= 1.0 - ParameterEpsilon)
            {
                return;
            }

            double distance = Math.Abs(GlyphVector.Cross(direction, point - origin)) / Math.Sqrt(lengthSquared);
            if (distance <= touchDistance)
            {
                splits.Add(t);
            }
        }

        private static int Winding(List<GlyphVector> starts, List<GlyphVector> ends, GlyphVector point)
        {
            int winding = 0;
            for (int i = 0; i < starts.Count; i++)
            {
                winding += GlyphPolygon.Crossing(starts[i], ends[i], point);
            }

            return winding;
        }

        private static List<List<int>> ChainLoops(List<Edge> edges, List<GlyphVector> vertices)
        {
            var outgoing = new Dictionary<int, List<int>>();
            for (int e = 0; e < edges.Count; e++)
            {
                if (!outgoing.TryGetValue(edges[e].From, out List<int> list))
                {
                    list = new List<int>();
                    outgoing.Add(edges[e].From, list);
                }

                list.Add(e);
            }

            var loops = new List<List<int>>();
            var used = new bool[edges.Count];
            for (int first = 0; first < edges.Count; first++)
            {
                if (used[first])
                {
                    continue;
                }

                var chain = new List<int>();
                int startVertex = edges[first].From;
                int edge = first;
                bool closed = false;
                for (int guard = 0; guard <= edges.Count && edge >= 0; guard++)
                {
                    used[edge] = true;
                    chain.Add(edges[edge].From);
                    int at = edges[edge].To;
                    if (at == startVertex)
                    {
                        closed = true;
                        break;
                    }

                    edge = NextEdge(edges, vertices, outgoing, used, edge, at);
                }

                if (closed)
                {
                    loops.Add(chain);
                }
            }

            return loops;
        }

        // Where boundaries meet at one vertex, take the sharpest left turn: with the fill on
        // the left, that closes the smallest loop and keeps two shapes that only touch apart.
        private static int NextEdge(
            List<Edge> edges,
            List<GlyphVector> vertices,
            Dictionary<int, List<int>> outgoing,
            bool[] used,
            int incoming,
            int at)
        {
            if (!outgoing.TryGetValue(at, out List<int> candidates))
            {
                return -1;
            }

            GlyphVector incomingDirection = vertices[at] - vertices[edges[incoming].From];
            int best = -1;
            double bestTurn = double.NegativeInfinity;
            foreach (int candidate in candidates)
            {
                if (used[candidate])
                {
                    continue;
                }

                GlyphVector direction = vertices[edges[candidate].To] - vertices[at];
                double turn = Math.Atan2(
                    GlyphVector.Cross(incomingDirection, direction),
                    GlyphVector.Dot(incomingDirection, direction));
                if (turn > bestTurn)
                {
                    bestTurn = turn;
                    best = candidate;
                }
            }

            return best;
        }

        private sealed class VertexWelder
        {
            private readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
            private readonly GlyphVector origin;
            private readonly double weldDistance;
            private readonly double cellSize;

            public VertexWelder(GlyphVector origin, double weldDistance)
            {
                this.origin = origin;
                this.weldDistance = weldDistance;
                cellSize = weldDistance * 2.0;
            }

            public List<GlyphVector> Vertices { get; } = new List<GlyphVector>();

            public int Weld(GlyphVector point)
            {
                long cellX = (long)Math.Floor((point.X - origin.X) / cellSize);
                long cellY = (long)Math.Floor((point.Y - origin.Y) / cellSize);
                for (long offsetX = -1; offsetX <= 1; offsetX++)
                {
                    for (long offsetY = -1; offsetY <= 1; offsetY++)
                    {
                        if (!cells.TryGetValue(Key(cellX + offsetX, cellY + offsetY), out List<int> bucket))
                        {
                            continue;
                        }

                        foreach (int index in bucket)
                        {
                            if (GlyphVector.Distance(Vertices[index], point) <= weldDistance)
                            {
                                return index;
                            }
                        }
                    }
                }

                int created = Vertices.Count;
                Vertices.Add(point);
                long key = Key(cellX, cellY);
                if (!cells.TryGetValue(key, out List<int> list))
                {
                    list = new List<int>();
                    cells.Add(key, list);
                }

                list.Add(created);
                return created;
            }

            private static long Key(long x, long y) => (x << 32) ^ (y & 0xFFFFFFFFL);
        }
    }
}
