using System;
using System.Collections.Generic;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>One filled piece of a glyph: a counter-clockwise outer loop and the clockwise holes inside it.</summary>
    public sealed class GlyphRegion
    {
        public GlyphRegion(List<GlyphVector> outer)
        {
            Outer = outer;
        }

        public List<GlyphVector> Outer { get; }

        public List<List<GlyphVector>> Holes { get; } = new List<List<GlyphVector>>();

        public int VertexCount
        {
            get
            {
                int count = Outer.Count;
                foreach (List<GlyphVector> hole in Holes)
                {
                    count += hole.Count;
                }

                return count;
            }
        }

        /// <summary>
        /// Triangles in one cap. Bridging a hole into the outer loop repeats two vertices,
        /// and a polygon of n vertices clips into n - 2 triangles.
        /// </summary>
        public int CapTriangleCount => VertexCount + 2 * Holes.Count - 2;
    }

    /// <summary>
    /// Outline edits made between the union and the mesh: rounding corners, fitting the
    /// triangle budget, and grouping loops into regions.
    /// </summary>
    public static class GlyphPolygonShaping
    {
        /// <summary>
        /// Replaces every corner that turns more than <paramref name="minimumTurnRadians"/>
        /// with a circular arc.
        /// </summary>
        /// <remarks>
        /// WaxCandy lights the walls from smooth vertex normals. A sharp corner would
        /// average two wall directions into one normal and shade both walls with a
        /// gradient, so corners are rounded in the outline instead. An arc never takes
        /// more than half of either neighbouring edge, so the radius shrinks where edges
        /// are short.
        /// </remarks>
        public static List<GlyphVector> RoundCorners(
            IReadOnlyList<GlyphVector> loop,
            double radius,
            double minimumTurnRadians,
            double maximumArcStepRadians)
        {
            int count = loop.Count;
            var result = new List<GlyphVector>(count * 2);
            if (radius <= 0.0 || count < 3)
            {
                result.AddRange(loop);
                return result;
            }

            for (int i = 0; i < count; i++)
            {
                GlyphVector previous = loop[(i + count - 1) % count];
                GlyphVector corner = loop[i];
                GlyphVector next = loop[(i + 1) % count];
                GlyphVector incoming = corner - previous;
                GlyphVector outgoing = next - corner;
                double incomingLength = incoming.Length;
                double outgoingLength = outgoing.Length;
                if (incomingLength <= 0.0 || outgoingLength <= 0.0)
                {
                    result.Add(corner);
                    continue;
                }

                incoming /= incomingLength;
                outgoing /= outgoingLength;
                double turn = Math.Atan2(GlyphVector.Cross(incoming, outgoing), GlyphVector.Dot(incoming, outgoing));
                double absoluteTurn = Math.Abs(turn);
                if (absoluteTurn < minimumTurnRadians || absoluteTurn > Math.PI - 1e-3)
                {
                    result.Add(corner);
                    continue;
                }

                double halfTurnTangent = Math.Tan(absoluteTurn * 0.5);
                double tangentLength = Math.Min(radius * halfTurnTangent, 0.5 * Math.Min(incomingLength, outgoingLength));
                double arcRadius = tangentLength / halfTurnTangent;
                GlyphVector arcStart = corner - incoming * tangentLength;
                GlyphVector center = arcStart + incoming.LeftNormal * (arcRadius * Math.Sign(turn));
                GlyphVector startOffset = arcStart - center;
                double startAngle = Math.Atan2(startOffset.Y, startOffset.X);
                int steps = Math.Max(1, (int)Math.Ceiling(absoluteTurn / maximumArcStepRadians));
                for (int step = 0; step <= steps; step++)
                {
                    double angle = startAngle + turn * step / steps;
                    result.Add(center + new GlyphVector(Math.Cos(angle), Math.Sin(angle)) * arcRadius);
                }
            }

            return result;
        }

        /// <summary>
        /// Removes the vertices that change the outline least (Visvalingam-Whyatt) until
        /// at most <paramref name="targetVertexCount"/> remain across all loops.
        /// </summary>
        /// <remarks>
        /// A removal is refused when the shortcut would cross another edge or leave a
        /// vertex on its wrong side, and every loop keeps at least three vertices, so the
        /// target can be missed. Loops are replaced in place.
        /// </remarks>
        /// <returns>Whether the target was reached.</returns>
        public static bool SimplifyToVertexCount(List<List<GlyphVector>> loops, int targetVertexCount)
        {
            int total = 0;
            foreach (List<GlyphVector> loop in loops)
            {
                total += loop.Count;
            }

            if (total <= targetVertexCount)
            {
                return true;
            }

            var points = new GlyphVector[total];
            var loopOf = new int[total];
            var previous = new int[total];
            var next = new int[total];
            var alive = new bool[total];
            var version = new int[total];
            var loopStarts = new int[loops.Count];
            var loopSizes = new int[loops.Count];
            int offset = 0;
            for (int l = 0; l < loops.Count; l++)
            {
                int count = loops[l].Count;
                loopStarts[l] = offset;
                loopSizes[l] = count;
                for (int i = 0; i < count; i++)
                {
                    int id = offset + i;
                    points[id] = loops[l][i];
                    loopOf[id] = l;
                    previous[id] = offset + (i + count - 1) % count;
                    next[id] = offset + (i + 1) % count;
                    alive[id] = true;
                }

                offset += count;
            }

            var heap = new AreaHeap(total * 2);
            for (int id = 0; id < total; id++)
            {
                heap.Push(EffectiveArea(id), id, 0);
            }

            int remaining = total;
            while (remaining > targetVertexCount && heap.TryPop(out int candidate, out int stamp))
            {
                if (!alive[candidate] || stamp != version[candidate] || loopSizes[loopOf[candidate]] <= 3)
                {
                    continue;
                }

                int before = previous[candidate];
                int after = next[candidate];
                if (!CanRemove(candidate, before, after))
                {
                    // Retried when a neighbour's removal changes this vertex's shortcut.
                    continue;
                }

                alive[candidate] = false;
                next[before] = after;
                previous[after] = before;
                loopSizes[loopOf[candidate]]--;
                remaining--;
                heap.Push(EffectiveArea(before), before, ++version[before]);
                heap.Push(EffectiveArea(after), after, ++version[after]);
            }

            for (int l = 0; l < loops.Count; l++)
            {
                int start = loopStarts[l];
                int end = l + 1 < loops.Count ? loopStarts[l + 1] : total;
                var rebuilt = new List<GlyphVector>(loopSizes[l]);
                for (int id = start; id < end; id++)
                {
                    if (alive[id])
                    {
                        rebuilt.Add(points[id]);
                    }
                }

                loops[l] = rebuilt;
            }

            return remaining <= targetVertexCount;

            double EffectiveArea(int id) =>
                Math.Abs(GlyphVector.Cross(points[id] - points[previous[id]], points[next[id]] - points[previous[id]])) * 0.5;

            bool CanRemove(int id, int before, int after)
            {
                GlyphVector a = points[before];
                GlyphVector removed = points[id];
                GlyphVector b = points[after];
                for (int other = 0; other < total; other++)
                {
                    if (!alive[other] || other == id)
                    {
                        continue;
                    }

                    if (other != before && other != after && GlyphPolygon.TriangleContains(a, removed, b, points[other]))
                    {
                        return false;
                    }

                    int otherNext = next[other];
                    if (other == before || other == after || otherNext == before || otherNext == after)
                    {
                        continue;
                    }

                    if (GlyphPolygon.SegmentsIntersect(a, b, points[other], points[otherNext]))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Groups union loops into regions: each clockwise hole joins the smallest
        /// counter-clockwise loop that contains it. A hole with no container is dropped.
        /// </summary>
        public static List<GlyphRegion> BuildRegions(IReadOnlyList<List<GlyphVector>> loops)
        {
            var regions = new List<GlyphRegion>();
            var outerAreas = new List<double>();
            foreach (List<GlyphVector> loop in loops)
            {
                double area = GlyphPolygon.SignedArea(loop);
                if (area > 0.0)
                {
                    regions.Add(new GlyphRegion(loop));
                    outerAreas.Add(area);
                }
            }

            foreach (List<GlyphVector> loop in loops)
            {
                double area = GlyphPolygon.SignedArea(loop);
                if (area >= 0.0)
                {
                    continue;
                }

                // Just off the middle of the first edge, on the empty side: holes keep the solid on their left.
                GlyphVector probe = GlyphVector.Lerp(loop[0], loop[1], 0.5) + (loop[1] - loop[0]).RightNormal * 1e-4;
                int owner = -1;
                for (int r = 0; r < regions.Count; r++)
                {
                    if (outerAreas[r] > -area &&
                        (owner < 0 || outerAreas[r] < outerAreas[owner]) &&
                        GlyphPolygon.ContainsEvenOdd(regions[r].Outer, probe))
                    {
                        owner = r;
                    }
                }

                if (owner >= 0)
                {
                    regions[owner].Holes.Add(loop);
                }
            }

            return regions;
        }

        private sealed class AreaHeap
        {
            private double[] keys;
            private int[] ids;
            private int[] stamps;
            private int count;

            public AreaHeap(int capacity)
            {
                capacity = Math.Max(4, capacity);
                keys = new double[capacity];
                ids = new int[capacity];
                stamps = new int[capacity];
            }

            public void Push(double key, int id, int stamp)
            {
                if (count == keys.Length)
                {
                    Array.Resize(ref keys, count * 2);
                    Array.Resize(ref ids, count * 2);
                    Array.Resize(ref stamps, count * 2);
                }

                int index = count++;
                while (index > 0)
                {
                    int parent = (index - 1) / 2;
                    if (keys[parent] <= key)
                    {
                        break;
                    }

                    Move(parent, index);
                    index = parent;
                }

                Set(index, key, id, stamp);
            }

            public bool TryPop(out int id, out int stamp)
            {
                if (count == 0)
                {
                    id = -1;
                    stamp = 0;
                    return false;
                }

                id = ids[0];
                stamp = stamps[0];
                count--;
                if (count == 0)
                {
                    return true;
                }

                double key = keys[count];
                int lastId = ids[count];
                int lastStamp = stamps[count];
                int index = 0;
                while (true)
                {
                    int child = 2 * index + 1;
                    if (child >= count)
                    {
                        break;
                    }

                    if (child + 1 < count && keys[child + 1] < keys[child])
                    {
                        child++;
                    }

                    if (key <= keys[child])
                    {
                        break;
                    }

                    Move(child, index);
                    index = child;
                }

                Set(index, key, lastId, lastStamp);
                return true;
            }

            private void Move(int from, int to)
            {
                keys[to] = keys[from];
                ids[to] = ids[from];
                stamps[to] = stamps[from];
            }

            private void Set(int index, double key, int id, int stamp)
            {
                keys[index] = key;
                ids[index] = id;
                stamps[index] = stamp;
            }
        }
    }
}
