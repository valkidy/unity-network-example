using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkExample.UnityDemo.LocalAgent
{
    /// <summary>
    /// Ground-plane queries over a <see cref="DetourNavMesh"/>: which polygon a
    /// point stands on, the nearest walkable point, a path of corners between two
    /// points, and random walkable points.
    /// </summary>
    /// <remarks>
    /// Lookups work in X/Z and take height from the mesh, which is enough for the
    /// single-layer meshes the catalog ships. Paths follow Detour's approach: A*
    /// across polygons with each polygon entered at the middle of its shared edge,
    /// then a funnel pass that pulls the path tight around the corners it has to
    /// turn. Not thread safe; the search buffers are reused between calls.
    /// </remarks>
    public sealed class DetourNavMeshQuery
    {
        private const float EdgeEpsilon = 1e-4f;

        private readonly DetourNavMesh mesh;
        private readonly float[] costs;
        private readonly int[] parents;
        private readonly Vector3[] entryPoints;
        private readonly bool[] closed;
        private readonly List<int> open = new List<int>();
        private readonly List<int> polygonPath = new List<int>();
        private readonly List<Vector3> portalLefts = new List<Vector3>();
        private readonly List<Vector3> portalRights = new List<Vector3>();
        private readonly float[] cumulativeAreas;

        public DetourNavMeshQuery(DetourNavMesh mesh)
        {
            this.mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
            int count = mesh.PolygonCount;
            costs = new float[count];
            parents = new int[count];
            entryPoints = new Vector3[count];
            closed = new bool[count];
            cumulativeAreas = new float[count];
            float total = 0f;
            for (int polygon = 0; polygon < count; polygon++)
            {
                total += mesh.GetPolygonArea(polygon);
                cumulativeAreas[polygon] = total;
            }
        }

        public DetourNavMesh Mesh => mesh;

        /// <summary>
        /// The polygon under <paramref name="point"/> in X/Z, or -1 off the mesh.
        /// Where polygons overlap in X/Z, the one nearest in height wins.
        /// </summary>
        public int FindPolygon(Vector3 point)
        {
            int best = -1;
            float bestHeight = float.PositiveInfinity;
            for (int polygon = 0; polygon < mesh.PolygonCount; polygon++)
            {
                Vector4 bounds = mesh.GetPolygonBoundsXZ(polygon);
                if (point.x < bounds.x - EdgeEpsilon || point.z < bounds.y - EdgeEpsilon ||
                    point.x > bounds.z + EdgeEpsilon || point.z > bounds.w + EdgeEpsilon ||
                    !ContainsXZ(polygon, point))
                    continue;
                float height = Mathf.Abs(HeightOn(polygon, point) - point.y);
                if (height < bestHeight)
                {
                    best = polygon;
                    bestHeight = height;
                }
            }
            return best;
        }

        /// <summary>
        /// <paramref name="point"/> itself, at mesh height, when it is on the mesh;
        /// otherwise the nearest point on the mesh's border in X/Z.
        /// </summary>
        public Vector3 ClosestPoint(Vector3 point, out int polygon)
        {
            polygon = FindPolygon(point);
            if (polygon >= 0)
                return new Vector3(point.x, HeightOn(polygon, point), point.z);

            Vector3 best = point;
            float bestDistance = float.PositiveInfinity;
            for (int candidate = 0; candidate < mesh.PolygonCount; candidate++)
            {
                int corners = mesh.GetPolygonVertexCount(candidate);
                for (int edge = 0; edge < corners; edge++)
                {
                    if (mesh.GetNeighbour(candidate, edge) >= 0) continue; // interior edges are never nearest
                    Vector3 onEdge = ClosestOnSegmentXZ(point, mesh.GetPolygonVertex(candidate, edge),
                        mesh.GetPolygonVertex(candidate, (edge + 1) % corners));
                    float distance = SqrDistanceXZ(point, onEdge);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = onEdge;
                        polygon = candidate;
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// Fills <paramref name="corners"/> with the points to walk through, from
        /// <paramref name="start"/> to <paramref name="end"/> inclusive. Either end
        /// off the mesh is first moved to the nearest walkable point. False when no
        /// walkable route connects them.
        /// </summary>
        public bool FindPath(Vector3 start, Vector3 end, List<Vector3> corners)
        {
            if (corners == null) throw new ArgumentNullException(nameof(corners));
            corners.Clear();
            start = ClosestPoint(start, out int startPolygon);
            end = ClosestPoint(end, out int endPolygon);
            if (startPolygon < 0 || endPolygon < 0 || !FindPolygonPath(startPolygon, endPolygon, start, end))
                return false;
            BuildPortals(start, end);
            PullString(corners);
            return true;
        }

        /// <summary>A uniformly distributed walkable point.</summary>
        public Vector3 RandomPoint(System.Random random)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            float total = cumulativeAreas[cumulativeAreas.Length - 1];
            float pick = (float)random.NextDouble() * total;
            int polygon = Array.BinarySearch(cumulativeAreas, pick);
            polygon = polygon >= 0 ? polygon : Mathf.Min(~polygon, cumulativeAreas.Length - 1);

            // A convex polygon is a fan of triangles from its first corner.
            int corners = mesh.GetPolygonVertexCount(polygon);
            Vector3 origin = mesh.GetPolygonVertex(polygon, 0);
            float fanTotal = 0f;
            for (int k = 1; k < corners - 1; k++)
                fanTotal += TriangleAreaXZ(origin, mesh.GetPolygonVertex(polygon, k), mesh.GetPolygonVertex(polygon, k + 1));
            float fanPick = (float)random.NextDouble() * fanTotal;
            int triangle = 1;
            for (; triangle < corners - 2; triangle++)
            {
                float area = TriangleAreaXZ(origin, mesh.GetPolygonVertex(polygon, triangle),
                    mesh.GetPolygonVertex(polygon, triangle + 1));
                if (fanPick <= area) break;
                fanPick -= area;
            }
            float u = (float)random.NextDouble();
            float v = (float)random.NextDouble();
            if (u + v > 1f)
            {
                u = 1f - u;
                v = 1f - v;
            }
            Vector3 b = mesh.GetPolygonVertex(polygon, triangle);
            Vector3 c = mesh.GetPolygonVertex(polygon, triangle + 1);
            return origin + (b - origin) * u + (c - origin) * v;
        }

        private bool FindPolygonPath(int startPolygon, int endPolygon, Vector3 start, Vector3 end)
        {
            polygonPath.Clear();
            for (int polygon = 0; polygon < mesh.PolygonCount; polygon++)
            {
                costs[polygon] = float.PositiveInfinity;
                parents[polygon] = -1;
                closed[polygon] = false;
            }
            open.Clear();
            costs[startPolygon] = 0f;
            entryPoints[startPolygon] = start;
            open.Add(startPolygon);

            while (open.Count > 0)
            {
                int bestIndex = 0;
                float bestScore = float.PositiveInfinity;
                for (int index = 0; index < open.Count; index++)
                {
                    int polygon = open[index];
                    float score = costs[polygon] + Vector3.Distance(entryPoints[polygon], end);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestIndex = index;
                    }
                }
                int current = open[bestIndex];
                open.RemoveAt(bestIndex);
                if (current == endPolygon)
                {
                    for (int polygon = endPolygon; polygon >= 0; polygon = parents[polygon])
                        polygonPath.Add(polygon);
                    polygonPath.Reverse();
                    return true;
                }
                closed[current] = true;

                int corners = mesh.GetPolygonVertexCount(current);
                for (int edge = 0; edge < corners; edge++)
                {
                    int next = mesh.GetNeighbour(current, edge);
                    if (next < 0 || closed[next]) continue;
                    Vector3 entry = (mesh.GetPolygonVertex(current, edge) +
                        mesh.GetPolygonVertex(current, (edge + 1) % corners)) * 0.5f;
                    float cost = costs[current] + Vector3.Distance(entryPoints[current], entry);
                    if (next == endPolygon) cost += Vector3.Distance(entry, end);
                    if (cost >= costs[next]) continue;
                    if (float.IsPositiveInfinity(costs[next])) open.Add(next);
                    costs[next] = cost;
                    parents[next] = current;
                    entryPoints[next] = entry;
                }
            }
            return false;
        }

        // Portal i is the edge crossed from polygonPath[i - 1] into polygonPath[i];
        // the first and last are the start and end points themselves.
        private void BuildPortals(Vector3 start, Vector3 end)
        {
            portalLefts.Clear();
            portalRights.Clear();
            portalLefts.Add(start);
            portalRights.Add(start);
            for (int index = 1; index < polygonPath.Count; index++)
            {
                int from = polygonPath[index - 1];
                int to = polygonPath[index];
                int corners = mesh.GetPolygonVertexCount(from);
                for (int edge = 0; edge < corners; edge++)
                {
                    if (mesh.GetNeighbour(from, edge) != to) continue;
                    Vector3 a = mesh.GetPolygonVertex(from, edge);
                    Vector3 b = mesh.GetPolygonVertex(from, (edge + 1) % corners);
                    Vector3 fromCenter = mesh.GetPolygonCenter(from);
                    Vector3 toCenter = mesh.GetPolygonCenter(to);
                    bool aIsLeft = Cross(fromCenter, toCenter, a) >= Cross(fromCenter, toCenter, b);
                    portalLefts.Add(aIsLeft ? a : b);
                    portalRights.Add(aIsLeft ? b : a);
                    break;
                }
            }
            portalLefts.Add(end);
            portalRights.Add(end);
        }

        // Simple stupid funnel algorithm (Mononen). Cross > 0 means left of the ray.
        private void PullString(List<Vector3> corners)
        {
            Vector3 apex = portalLefts[0];
            Vector3 left = apex;
            Vector3 right = apex;
            int apexIndex = 0, leftIndex = 0, rightIndex = 0;
            corners.Add(apex);
            for (int index = 1; index < portalLefts.Count; index++)
            {
                Vector3 portalLeft = portalLefts[index];
                Vector3 portalRight = portalRights[index];

                if (Cross(apex, right, portalRight) >= 0f)
                {
                    if (SameXZ(apex, right) || Cross(apex, left, portalRight) < 0f)
                    {
                        right = portalRight;
                        rightIndex = index;
                    }
                    else
                    {
                        // The right side crossed the left: the left corner is on the path.
                        apex = left;
                        apexIndex = leftIndex;
                        AddCorner(corners, apex);
                        left = right = apex;
                        leftIndex = rightIndex = apexIndex;
                        index = apexIndex;
                        continue;
                    }
                }

                if (Cross(apex, left, portalLeft) <= 0f)
                {
                    if (SameXZ(apex, left) || Cross(apex, right, portalLeft) > 0f)
                    {
                        left = portalLeft;
                        leftIndex = index;
                    }
                    else
                    {
                        apex = right;
                        apexIndex = rightIndex;
                        AddCorner(corners, apex);
                        left = right = apex;
                        leftIndex = rightIndex = apexIndex;
                        index = apexIndex;
                    }
                }
            }
            AddCorner(corners, portalLefts[portalLefts.Count - 1]);
        }

        private static void AddCorner(List<Vector3> corners, Vector3 corner)
        {
            if (!SameXZ(corners[corners.Count - 1], corner))
                corners.Add(corner);
        }

        private bool ContainsXZ(int polygon, Vector3 point)
        {
            int corners = mesh.GetPolygonVertexCount(polygon);
            bool anyPositive = false, anyNegative = false;
            for (int k = 0; k < corners; k++)
            {
                float side = Cross(mesh.GetPolygonVertex(polygon, k),
                    mesh.GetPolygonVertex(polygon, (k + 1) % corners), point);
                if (side > EdgeEpsilon) anyPositive = true;
                else if (side < -EdgeEpsilon) anyNegative = true;
                if (anyPositive && anyNegative) return false;
            }
            return true;
        }

        // Height of the polygon's surface under the point, from the fan triangle it falls in.
        private float HeightOn(int polygon, Vector3 point)
        {
            int corners = mesh.GetPolygonVertexCount(polygon);
            Vector3 a = mesh.GetPolygonVertex(polygon, 0);
            for (int k = 1; k < corners - 1; k++)
            {
                Vector3 b = mesh.GetPolygonVertex(polygon, k);
                Vector3 c = mesh.GetPolygonVertex(polygon, k + 1);
                float denominator = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                if (Mathf.Abs(denominator) < 1e-8f) continue;
                float wa = ((b.z - c.z) * (point.x - c.x) + (c.x - b.x) * (point.z - c.z)) / denominator;
                float wb = ((c.z - a.z) * (point.x - c.x) + (a.x - c.x) * (point.z - c.z)) / denominator;
                float wc = 1f - wa - wb;
                if (wa >= -EdgeEpsilon && wb >= -EdgeEpsilon && wc >= -EdgeEpsilon)
                    return wa * a.y + wb * b.y + wc * c.y;
            }
            return mesh.GetPolygonCenter(polygon).y;
        }

        private static Vector3 ClosestOnSegmentXZ(Vector3 point, Vector3 a, Vector3 b)
        {
            float dx = b.x - a.x, dz = b.z - a.z;
            float lengthSquared = dx * dx + dz * dz;
            float t = lengthSquared <= 0f ? 0f
                : Mathf.Clamp01(((point.x - a.x) * dx + (point.z - a.z) * dz) / lengthSquared);
            return a + (b - a) * t;
        }

        private static float Cross(Vector3 a, Vector3 b, Vector3 c) =>
            (b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x);
        private static float TriangleAreaXZ(Vector3 a, Vector3 b, Vector3 c) => Mathf.Abs(Cross(a, b, c)) * 0.5f;
        private static float SqrDistanceXZ(Vector3 a, Vector3 b) =>
            (a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z);
        private static bool SameXZ(Vector3 a, Vector3 b) => SqrDistanceXZ(a, b) < 1e-8f;
    }
}
