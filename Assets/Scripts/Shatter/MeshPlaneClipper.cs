using System.Collections.Generic;
using NetworkExample.UnityDemo.Text3D;
using UnityEngine;

namespace NetworkExample.UnityDemo.Shatter
{
    /// <summary>
    /// Cuts a mesh with a plane, keeping what is behind it and closing the cut
    /// with a face.
    /// </summary>
    /// <remarks>
    /// This is the one operation a fracture is made of: a cell of a voronoi
    /// diagram is what is left of a piece after it has been cut by the plane
    /// halfway to every other cell, so a clipper and a set of points is a
    /// fracture.
    ///
    /// The cut is closed rather than left open because the model is a closed
    /// surface: a piece of a closed surface that is not closed again shows its
    /// inside, which URP's single-sided shading draws as a hole. The cap is the
    /// cross-section the plane took, triangulated by
    /// <see cref="GlyphTriangulator"/> -- the same ear clipper the glyph caps use,
    /// for the same shape of problem: a loop, possibly with loops inside it, that
    /// has to be filled using nothing but the points already on it.
    ///
    /// Filling from the loop alone is also what gives the cap its texture. Every
    /// cap vertex is a point that was on the model's surface a moment ago and
    /// still carries that surface's UV, so a cut through a wall wears the wall's
    /// own colours smeared across it. It is not what the inside of a wall looks
    /// like, and for debris in flight for a second and a half, nobody reads it as
    /// wrong -- and the piece needs no material of its own, which is what keeps a
    /// hundred pieces batching as one.
    ///
    /// Where the model was already open -- 52 of the tower's 108 parts have a rim
    /// somewhere -- a cut runs off the edge and the loop never closes. Those are
    /// counted and left open rather than guessed at, because closing them means
    /// inventing an edge the model never had.
    /// </remarks>
    public static class MeshPlaneClipper
    {
        /// <summary>
        /// How close to the plane a vertex counts as being on it. A cut that
        /// passed either side of such a vertex would make a second vertex a
        /// thousandth of a millimetre away, and the cap's loops would not meet
        /// there.
        /// </summary>
        public const float OnPlaneEpsilon = 1e-5f;

        /// <summary>
        /// Everything behind <paramref name="plane"/>, capped where the plane cut
        /// it. Returns null when the plane took everything.
        /// </summary>
        /// <param name="unclosedCuts">
        /// Cut loops that could not be closed, which is what the plane leaving
        /// through a rim the model already had looks like from here.
        /// </param>
        public static ShatterGeometry Clip(
            ShatterGeometry source,
            Plane plane,
            out int unclosedCuts)
        {
            unclosedCuts = 0;
            if (source == null || source.TriangleCount == 0)
            {
                return null;
            }

            var distances = new float[source.VertexCount];
            bool anyKept = false;
            bool anyCut = false;
            for (int index = 0; index < distances.Length; ++index)
            {
                float distance = plane.GetDistanceToPoint(source.Positions[index]);
                distances[index] = distance;
                anyKept |= distance <= OnPlaneEpsilon;
                anyCut |= distance > OnPlaneEpsilon;
            }

            if (!anyKept)
            {
                return null;
            }

            if (!anyCut)
            {
                return source;
            }

            var state = new ClipState(source, distances);
            List<int> triangles = source.Triangles;
            for (int start = 0; start + 2 < triangles.Count; start += 3)
            {
                ClipTriangle(
                    state,
                    triangles[start],
                    triangles[start + 1],
                    triangles[start + 2]);
            }

            if (state.Result.TriangleCount == 0)
            {
                return null;
            }

            Cap(state, plane, out unclosedCuts);
            return state.Result;
        }

        private static void ClipTriangle(ClipState state, int a, int b, int c)
        {
            int mask = (state.IsKept(a) ? 1 : 0) |
                (state.IsKept(b) ? 2 : 0) |
                (state.IsKept(c) ? 4 : 0);
            switch (mask)
            {
                case 0:
                    return;
                case 7:
                    Emit(state, state.Keep(a), state.Keep(b), state.Keep(c));
                    return;
                case 1:
                    ClipToCorner(state, a, b, c);
                    return;
                case 2:
                    ClipToCorner(state, b, c, a);
                    return;
                case 4:
                    ClipToCorner(state, c, a, b);
                    return;
                case 3:
                    ClipToEdge(state, a, b, c);
                    return;
                case 6:
                    ClipToEdge(state, b, c, a);
                    return;
                default:
                    ClipToEdge(state, c, a, b);
                    return;
            }
        }

        /// <summary>
        /// One corner kept: the triangle comes back as a smaller triangle on the
        /// same corner.
        /// </summary>
        private static void ClipToCorner(ClipState state, int kept, int gone, int alsoGone)
        {
            int keptIndex = state.Keep(kept);
            int onFirstEdge = state.Cross(kept, gone);
            int onLastEdge = state.Cross(kept, alsoGone);
            Emit(state, keptIndex, onFirstEdge, onLastEdge);
            // The kept surface runs from the first cut point to the last; the cap
            // closes the same gap from the other side, so it is that edge
            // reversed.
            state.AddCutEdge(onLastEdge, onFirstEdge);
        }

        /// <summary>
        /// One corner lost: the triangle comes back as the quad that is left.
        /// </summary>
        private static void ClipToEdge(ClipState state, int kept, int alsoKept, int gone)
        {
            int keptIndex = state.Keep(kept);
            int alsoKeptIndex = state.Keep(alsoKept);
            int onFirstEdge = state.Cross(alsoKept, gone);
            int onLastEdge = state.Cross(kept, gone);
            Emit(state, keptIndex, alsoKeptIndex, onFirstEdge);
            Emit(state, keptIndex, onFirstEdge, onLastEdge);
            state.AddCutEdge(onLastEdge, onFirstEdge);
        }

        /// <summary>
        /// Adds a triangle, unless the cut left it with no area.
        /// </summary>
        /// <remarks>
        /// A cut passing exactly through a corner keeps a triangle that is a
        /// sliver of nothing, either because two of its corners are the same
        /// vertex or because they are two vertices standing in the same place --
        /// which is what a rim and the cap welded to it are. Neither draws
        /// anything, and both leave an edge that looks like a tear to anything
        /// counting how many faces use it.
        /// </remarks>
        private static void Emit(ClipState state, int a, int b, int c)
        {
            if (a == b || b == c || c == a)
            {
                return;
            }

            List<Vector3> positions = state.Result.Positions;
            Vector3 first = positions[a];
            Vector3 area = Vector3.Cross(positions[b] - first, positions[c] - first);
            if (area.sqrMagnitude < 1e-16f)
            {
                return;
            }

            state.Result.AddTriangle(a, b, c);
        }

        private static void Cap(ClipState state, Plane plane, out int unclosedCuts)
        {
            List<List<int>> loops = state.ChainCutEdges(out unclosedCuts);
            if (loops.Count == 0)
            {
                return;
            }

            BuildPlaneBasis(plane.normal, out Vector3 right, out Vector3 up);
            var outerLoops = new List<List<GlyphVector>>();
            var outerIndices = new List<List<int>>();
            var holeLoops = new List<List<GlyphVector>>();
            var holeIndices = new List<List<int>>();
            for (int index = 0; index < loops.Count; ++index)
            {
                List<int> loop = loops[index];
                var projected = new List<GlyphVector>(loop.Count);
                for (int step = 0; step < loop.Count; ++step)
                {
                    Vector3 position = state.Result.Positions[loop[step]];
                    projected.Add(new GlyphVector(
                        Vector3.Dot(position, right),
                        Vector3.Dot(position, up)));
                }

                // A loop that came back clockwise encloses no material of its
                // own: it is the far side of a hollow the cut ran through, and
                // the cap has to leave it empty.
                if (GlyphPolygon.SignedArea(projected) >= 0.0)
                {
                    outerLoops.Add(projected);
                    outerIndices.Add(loop);
                }
                else
                {
                    holeLoops.Add(projected);
                    holeIndices.Add(loop);
                }
            }

            for (int index = 0; index < outerLoops.Count; ++index)
            {
                CapOneLoop(
                    state,
                    plane.normal,
                    outerLoops[index],
                    outerIndices[index],
                    holeLoops,
                    holeIndices,
                    outerLoops);
            }
        }

        private static void CapOneLoop(
            ClipState state,
            Vector3 capNormal,
            List<GlyphVector> outer,
            List<int> outerIndices,
            List<List<GlyphVector>> holeLoops,
            List<List<int>> holeIndices,
            List<List<GlyphVector>> allOuterLoops)
        {
            var region = new GlyphRegion(outer);
            var loopStarts = new List<int> { state.Result.VertexCount };
            state.AddCapVertices(outerIndices, capNormal);

            for (int index = 0; index < holeLoops.Count; ++index)
            {
                if (!GlyphPolygon.ContainsEvenOdd(outer, holeLoops[index][0]) ||
                    !IsInnermostContainer(outer, holeLoops[index][0], allOuterLoops))
                {
                    continue;
                }

                region.Holes.Add(holeLoops[index]);
                loopStarts.Add(state.Result.VertexCount);
                state.AddCapVertices(holeIndices[index], capNormal);
            }

            var capTriangles = new List<int>();
            GlyphTriangulator.Triangulate(region, loopStarts, capTriangles);
            for (int index = 0; index + 2 < capTriangles.Count; index += 3)
            {
                state.Result.AddTriangle(
                    capTriangles[index],
                    capTriangles[index + 1],
                    capTriangles[index + 2]);
            }
        }

        /// <summary>
        /// Whether <paramref name="outer"/> is the smallest of the cross-sections
        /// holding <paramref name="point"/>.
        /// </summary>
        /// <remarks>
        /// A plane through a model with a hollow inside another hollow leaves
        /// cross-sections nested in each other, and a hole belongs to the
        /// tightest one around it -- given to any looser one as well, the cap
        /// would be cut away twice.
        /// </remarks>
        private static bool IsInnermostContainer(
            List<GlyphVector> outer,
            GlyphVector point,
            List<List<GlyphVector>> allOuterLoops)
        {
            double area = GlyphPolygon.SignedArea(outer);
            for (int index = 0; index < allOuterLoops.Count; ++index)
            {
                List<GlyphVector> other = allOuterLoops[index];
                if (ReferenceEquals(other, outer) ||
                    !GlyphPolygon.ContainsEvenOdd(other, point))
                {
                    continue;
                }

                if (GlyphPolygon.SignedArea(other) < area)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Two axes across the plane, turned so that a loop counter-clockwise in
        /// them faces along <paramref name="normal"/>.
        /// </summary>
        public static void BuildPlaneBasis(Vector3 normal, out Vector3 right, out Vector3 up)
        {
            Vector3 seed = Mathf.Abs(normal.x) < 0.9f ? Vector3.right : Vector3.up;
            right = Vector3.Normalize(Vector3.Cross(seed, normal));
            up = Vector3.Cross(normal, right);
        }

        private sealed class ClipState
        {
            private readonly ShatterGeometry source;
            private readonly float[] distances;
            private readonly int[] mapped;
            private readonly Dictionary<long, int> crossings = new Dictionary<long, int>();
            private readonly List<int> cutFrom = new List<int>();
            private readonly List<int> cutTo = new List<int>();

            public ClipState(ShatterGeometry source, float[] distances)
            {
                this.source = source;
                this.distances = distances;
                Result = new ShatterGeometry(source.CarriesNormals, source.CarriesUvs);
                mapped = new int[source.VertexCount];
                for (int index = 0; index < mapped.Length; ++index)
                {
                    mapped[index] = -1;
                }
            }

            public ShatterGeometry Result { get; }

            public bool IsKept(int index)
            {
                return distances[index] <= OnPlaneEpsilon;
            }

            /// <summary>
            /// The kept vertex's place in the result, put there the first time it
            /// is asked for so nothing the cut removed is carried over.
            /// </summary>
            public int Keep(int index)
            {
                if (mapped[index] < 0)
                {
                    mapped[index] = Result.VertexCount;
                    Result.Add(
                        source.Positions[index],
                        source.Normals[index],
                        source.Uvs[index]);
                }

                return mapped[index];
            }

            /// <summary>
            /// Where the edge between a kept and a removed vertex meets the
            /// plane, made once and shared by both triangles along that edge --
            /// which is what lets the cut edges be chained into loops by index.
            /// </summary>
            public int Cross(int kept, int removed)
            {
                // A kept vertex already on the plane is the crossing itself.
                if (distances[kept] >= -OnPlaneEpsilon)
                {
                    return Keep(kept);
                }

                long key = kept < removed
                    ? ((long)kept << 32) | (uint)removed
                    : ((long)removed << 32) | (uint)kept;
                if (crossings.TryGetValue(key, out int existing))
                {
                    return existing;
                }

                float travel = distances[kept] / (distances[kept] - distances[removed]);
                int created = Result.VertexCount;
                Vector3 normal = Vector3.Lerp(
                    source.Normals[kept],
                    source.Normals[removed],
                    travel);
                Result.Add(
                    Vector3.Lerp(source.Positions[kept], source.Positions[removed], travel),
                    normal.sqrMagnitude > 1e-12f ? normal.normalized : normal,
                    Vector2.Lerp(source.Uvs[kept], source.Uvs[removed], travel));
                crossings.Add(key, created);
                return created;
            }

            public void AddCutEdge(int from, int to)
            {
                if (from == to)
                {
                    return;
                }

                cutFrom.Add(from);
                cutTo.Add(to);
            }

            /// <summary>
            /// Copies a loop's vertices for the cap to use, facing out of the cut
            /// rather than along the surface they were on.
            /// </summary>
            /// <remarks>
            /// Copied rather than shared, because a vertex on the rim of the cut
            /// belongs to two faces that meet at an angle: the wall, which keeps
            /// the model's own normal, and the cap, which is flat. One vertex
            /// cannot carry both, and sharing it rounds the corner off.
            /// </remarks>
            public void AddCapVertices(List<int> loop, Vector3 capNormal)
            {
                for (int index = 0; index < loop.Count; ++index)
                {
                    int vertex = loop[index];
                    Result.Add(
                        Result.Positions[vertex],
                        capNormal,
                        Result.Uvs[vertex]);
                }
            }

            /// <summary>
            /// Walks the cut edges into closed loops. Anything that runs into a
            /// dead end is counted and dropped.
            /// </summary>
            /// <remarks>
            /// Chained by where a point is rather than by which vertex it is. A
            /// cap made by an earlier cut is a surface of its own -- it repeats
            /// the rim's vertices to give them the cap's flat normal -- so the
            /// next cut through that rim produces two cut points in the same
            /// place with different indices. Chaining by index stops dead at
            /// every one of them; chaining by position walks straight through.
            /// </remarks>
            public List<List<int>> ChainCutEdges(out int unclosedCuts)
            {
                unclosedCuts = 0;
                var atPoint = new Dictionary<Vector3, int>(cutFrom.Count * 2);
                var representative = new List<int>();
                var next = new Dictionary<int, int>(cutFrom.Count);
                var starts = new List<int>(cutFrom.Count);
                for (int index = 0; index < cutFrom.Count; ++index)
                {
                    int from = PointAt(cutFrom[index], atPoint, representative);
                    int to = PointAt(cutTo[index], atPoint, representative);
                    if (from == to)
                    {
                        continue;
                    }

                    starts.Add(from);
                    if (!next.ContainsKey(from))
                    {
                        next.Add(from, to);
                        continue;
                    }

                    // Two cut edges leaving the same point: the cross-section
                    // pinches to nothing there, and which way to go on is not
                    // decidable from the edges alone.
                    ++unclosedCuts;
                }

                var loops = new List<List<int>>();
                var visited = new HashSet<int>();
                for (int index = 0; index < starts.Count; ++index)
                {
                    int start = starts[index];
                    if (visited.Contains(start))
                    {
                        continue;
                    }

                    var loop = new List<int>();
                    int step = start;
                    bool closed = false;
                    while (visited.Add(step))
                    {
                        loop.Add(representative[step]);
                        if (!next.TryGetValue(step, out step))
                        {
                            break;
                        }

                        if (step == start)
                        {
                            closed = true;
                            break;
                        }
                    }

                    if (closed && loop.Count >= 3)
                    {
                        loops.Add(loop);
                    }
                    else
                    {
                        ++unclosedCuts;
                    }
                }

                return loops;
            }

            /// <summary>
            /// The id of the place a vertex stands in, shared by every vertex
            /// standing in exactly that place.
            /// </summary>
            private int PointAt(
                int vertex,
                Dictionary<Vector3, int> atPoint,
                List<int> representative)
            {
                Vector3 position = Result.Positions[vertex];
                // Negative zero compares equal to zero and need not hash the
                // same, and a cut plane through an axis produces plenty of both.
                var key = new Vector3(
                    position.x == 0f ? 0f : position.x,
                    position.y == 0f ? 0f : position.y,
                    position.z == 0f ? 0f : position.z);
                if (atPoint.TryGetValue(key, out int existing))
                {
                    return existing;
                }

                int created = representative.Count;
                representative.Add(vertex);
                atPoint.Add(key, created);
                return created;
            }
        }
    }
}
