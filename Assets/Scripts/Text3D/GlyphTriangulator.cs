using System;
using System.Collections.Generic;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>
    /// Ear-clipping triangulation of one glyph region for the front and back caps.
    /// </summary>
    /// <remarks>
    /// Each hole is joined to the outer loop by a two-way bridge to the nearest vertex it
    /// can see, turning the region into one polygon that touches itself along the
    /// bridge. The bridge repeats two vertices but reuses their indices, so the cap stays
    /// welded to the wall rings.
    ///
    /// Only boundary vertices are used, as in the WaxCandy reference letter: the colour
    /// layers come from the edge distance map, not from interior vertices.
    /// </remarks>
    public static class GlyphTriangulator
    {
        /// <param name="loopStartIndices">Mesh index of the first vertex of the outer loop, then of each hole.</param>
        /// <param name="triangles">Receives triangles that are counter-clockwise in XY.</param>
        /// <returns>
        /// False when a hole could not be bridged or clipping had to force a triangle,
        /// which only happens for an outline that crosses itself.
        /// </returns>
        public static bool Triangulate(GlyphRegion region, IReadOnlyList<int> loopStartIndices, List<int> triangles)
        {
            if (region == null)
            {
                throw new ArgumentNullException(nameof(region));
            }

            Node outer = BuildLoop(region.Outer, loopStartIndices[0], out _);
            if (outer == null)
            {
                return false;
            }

            int nodeCount = region.Outer.Count;
            var holes = new List<(Node rightmost, int size)>(region.Holes.Count);
            for (int h = 0; h < region.Holes.Count; h++)
            {
                if (BuildLoop(region.Holes[h], loopStartIndices[h + 1], out Node rightmost) != null)
                {
                    holes.Add((rightmost, region.Holes[h].Count));
                }
            }

            holes.Sort((a, b) => b.rightmost.Position.X.CompareTo(a.rightmost.Position.X));
            bool bridged = true;
            for (int k = 0; k < holes.Count; k++)
            {
                Node bridge = FindBridge(outer, holes[k].rightmost, holes, k + 1);
                if (bridge == null)
                {
                    bridged = false;
                    continue;
                }

                Splice(bridge, holes[k].rightmost);
                nodeCount += holes[k].size + 2;
            }

            return ClipEars(outer, nodeCount, triangles) && bridged;
        }

        private static Node BuildLoop(IReadOnlyList<GlyphVector> loop, int startIndex, out Node rightmost)
        {
            rightmost = null;
            if (loop.Count < 3)
            {
                return null;
            }

            Node first = null;
            Node last = null;
            for (int i = 0; i < loop.Count; i++)
            {
                var node = new Node(loop[i], startIndex + i);
                if (first == null)
                {
                    first = node;
                }
                else
                {
                    last.Next = node;
                    node.Previous = last;
                }

                last = node;
                if (rightmost == null ||
                    node.Position.X > rightmost.Position.X ||
                    (node.Position.X == rightmost.Position.X && node.Position.Y < rightmost.Position.Y))
                {
                    rightmost = node;
                }
            }

            last.Next = first;
            first.Previous = last;
            return first;
        }

        private static Node FindBridge(Node outer, Node hole, List<(Node rightmost, int size)> holes, int firstUnbridged)
        {
            GlyphVector origin = hole.Position;
            var candidates = new List<Node>();
            Node node = outer;
            do
            {
                candidates.Add(node);
                node = node.Next;
            }
            while (node != outer);

            candidates.Sort((a, b) => (a.Position - origin).LengthSquared.CompareTo((b.Position - origin).LengthSquared));
            foreach (Node candidate in candidates)
            {
                GlyphVector target = candidate.Position;
                if ((target - origin).LengthSquared <= 0.0 ||
                    !IsLocallyInside(candidate, origin) ||
                    !IsLocallyInside(hole, target) ||
                    CrossesLoop(outer, origin, target) ||
                    CrossesLoop(hole, origin, target))
                {
                    continue;
                }

                bool blocked = false;
                for (int k = firstUnbridged; k < holes.Count && !blocked; k++)
                {
                    blocked = CrossesLoop(holes[k].rightmost, origin, target);
                }

                if (!blocked)
                {
                    return candidate;
                }
            }

            return null;
        }

        // The loop runs bridge -> hole -> around the hole -> hole copy -> bridge copy -> on.
        private static void Splice(Node bridge, Node hole)
        {
            var bridgeCopy = new Node(bridge.Position, bridge.Index);
            var holeCopy = new Node(hole.Position, hole.Index);
            Node afterBridge = bridge.Next;
            Node beforeHole = hole.Previous;

            bridge.Next = hole;
            hole.Previous = bridge;
            beforeHole.Next = holeCopy;
            holeCopy.Previous = beforeHole;
            holeCopy.Next = bridgeCopy;
            bridgeCopy.Previous = holeCopy;
            bridgeCopy.Next = afterBridge;
            afterBridge.Previous = bridgeCopy;
        }

        // Whether a direction from the node toward the point starts inside the solid, which lies left of every edge.
        private static bool IsLocallyInside(Node node, GlyphVector point)
        {
            GlyphVector previous = node.Previous.Position;
            GlyphVector current = node.Position;
            GlyphVector next = node.Next.Position;
            bool leftOfIncoming = GlyphVector.Cross(current - previous, point - previous) > 0.0;
            bool leftOfOutgoing = GlyphVector.Cross(next - current, point - current) > 0.0;
            bool convex = GlyphVector.Cross(current - previous, next - current) >= 0.0;
            return convex ? leftOfIncoming && leftOfOutgoing : leftOfIncoming || leftOfOutgoing;
        }

        private static bool CrossesLoop(Node start, GlyphVector a, GlyphVector b)
        {
            Node node = start;
            do
            {
                GlyphVector c = node.Position;
                GlyphVector d = node.Next.Position;
                bool sharesEndpoint = c.Equals(a) || c.Equals(b) || d.Equals(a) || d.Equals(b);
                if (!sharesEndpoint && GlyphPolygon.SegmentsIntersect(a, b, c, d))
                {
                    return true;
                }

                node = node.Next;
            }
            while (node != start);

            return false;
        }

        private static bool ClipEars(Node start, int count, List<int> triangles)
        {
            bool clean = true;
            Node current = start;
            int remaining = count;
            int misses = 0;
            while (remaining > 3)
            {
                if (IsEar(current))
                {
                    Emit(current, triangles);
                    current = Remove(current);
                    remaining--;
                    misses = 0;
                    continue;
                }

                current = current.Next;
                if (++misses >= remaining)
                {
                    // A simple polygon always has an ear, so this means rounding or
                    // simplification left the outline crossing itself. Clip the most
                    // convex corner anyway so the cap still closes.
                    current = MostConvex(current);
                    Emit(current, triangles);
                    current = Remove(current);
                    remaining--;
                    misses = 0;
                    clean = false;
                }
            }

            Emit(current, triangles);
            return clean;
        }

        private static bool IsEar(Node ear)
        {
            GlyphVector a = ear.Previous.Position;
            GlyphVector b = ear.Position;
            GlyphVector c = ear.Next.Position;
            double turn = GlyphVector.Cross(b - a, c - b);
            if (turn < 0.0)
            {
                return false;
            }

            if (turn == 0.0)
            {
                // A vertex in the middle of a straight run clips into a zero-area triangle,
                // which keeps the cap's edges matched to the walls; a spike does not.
                return GlyphVector.Dot(b - a, c - b) > 0.0;
            }

            for (Node node = ear.Next.Next; node != ear.Previous; node = node.Next)
            {
                GlyphVector point = node.Position;
                if (point.Equals(a) || point.Equals(b) || point.Equals(c))
                {
                    continue;
                }

                // Only a reflex vertex can be the one that makes this corner not an ear.
                if (GlyphVector.Cross(point - node.Previous.Position, node.Next.Position - point) > 0.0)
                {
                    continue;
                }

                if (GlyphVector.Cross(b - a, point - a) >= 0.0 &&
                    GlyphVector.Cross(c - b, point - b) >= 0.0 &&
                    GlyphVector.Cross(a - c, point - c) >= 0.0)
                {
                    return false;
                }
            }

            return true;
        }

        private static Node MostConvex(Node start)
        {
            Node best = start;
            double bestTurn = double.NegativeInfinity;
            Node node = start;
            do
            {
                GlyphVector incoming = node.Position - node.Previous.Position;
                GlyphVector outgoing = node.Next.Position - node.Position;
                double turn = GlyphVector.Cross(incoming, outgoing) / (incoming.Length * outgoing.Length + 1e-30);
                if (turn > bestTurn)
                {
                    bestTurn = turn;
                    best = node;
                }

                node = node.Next;
            }
            while (node != start);

            return best;
        }

        private static void Emit(Node node, List<int> triangles)
        {
            triangles.Add(node.Previous.Index);
            triangles.Add(node.Index);
            triangles.Add(node.Next.Index);
        }

        private static Node Remove(Node node)
        {
            node.Previous.Next = node.Next;
            node.Next.Previous = node.Previous;
            return node.Next;
        }

        private sealed class Node
        {
            public readonly GlyphVector Position;
            public readonly int Index;
            public Node Previous;
            public Node Next;

            public Node(GlyphVector position, int index)
            {
                Position = position;
                Index = index;
            }
        }
    }
}
