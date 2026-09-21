using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using NetworkExample.UnityDemo.Common;
using NetworkExample.UnityDemo.LocalAgent;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class DetourNavMeshTests
    {
        private const int TileOffset = 88;

        private static byte[] Bundle()
        {
            Assert.That(NetworkGameplayCatalogBundle.TryLoadDefault(out byte[] bundle, out _), Is.True);
            return bundle;
        }

        internal static byte[] Artifact(string name)
        {
            using (var archive = new ZipArchive(new MemoryStream(Bundle(), false), ZipArchiveMode.Read))
            using (Stream entry = archive.GetEntry("mesh_assets/recast/" + name + ".navmesh").Open())
            using (var copy = new MemoryStream())
            {
                entry.CopyTo(copy);
                return copy.ToArray();
            }
        }

        private static DetourNavMesh Parse(byte[] artifact)
        {
            Assert.That(DetourNavMesh.TryParse(artifact, out DetourNavMesh mesh, out string diagnostic),
                Is.True, diagnostic);
            return mesh;
        }

        internal static DetourNavMeshQuery Plane() => new DetourNavMeshQuery(Parse(Artifact("plane_200x200")));
        internal static DetourNavMeshQuery Undulating() => new DetourNavMeshQuery(Parse(Artifact("undulating")));

        private static void AssertRejected(byte[] artifact, string expected)
        {
            Assert.That(DetourNavMesh.TryParse(artifact, out DetourNavMesh mesh, out string diagnostic), Is.False);
            Assert.That(mesh, Is.Null);
            Assert.That(diagnostic, Does.Contain(expected));
        }

        // Every sampled point along the path must stand on the mesh.
        private static void AssertWalkable(DetourNavMeshQuery query, List<Vector3> corners)
        {
            for (int index = 1; index < corners.Count; index++)
            {
                Vector3 a = corners[index - 1], b = corners[index];
                int samples = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(a, b) / 0.25f));
                for (int s = 0; s <= samples; s++)
                {
                    Vector3 point = Vector3.Lerp(a, b, s / (float)samples);
                    Vector3 onMesh = query.ClosestPoint(point, out _);
                    Assert.That(new Vector2(point.x - onMesh.x, point.z - onMesh.z).magnitude,
                        Is.LessThan(0.01f), "segment " + index + " leaves the mesh at " + point);
                }
            }
        }

        [Test]
        public void TheCatalogsNavigationMeshIsTheOneTheServerPatrolsUse()
        {
            Assert.That(NetworkGameplayCatalogBundle.TryLoadDefault(out byte[] bundle, out string entryPath), Is.True);
            Assert.That(NetworkGameplayCatalogBundle.TryReadNavigationMesh(bundle, entryPath,
                out byte[] bytes, out string diagnostic), Is.True, diagnostic);
            Assert.That(bytes, Is.EqualTo(Artifact("plane_200x200")));
        }

        [Test]
        public void ParsesTheFlatMapAsBaked()
        {
            DetourNavMesh mesh = Parse(Artifact("plane_200x200"));
            Assert.That(mesh.PolygonCount, Is.EqualTo(65));
            Assert.That(mesh.VertexCount, Is.EqualTo(128));
            Assert.That(mesh.BoundsMin, Is.EqualTo(new Vector3(-100f, 0f, -100f)));
            Assert.That(mesh.BoundsMax, Is.EqualTo(new Vector3(100f, 2f, 100f)));
            Assert.That(mesh.AgentRadius, Is.EqualTo(0.6f).Within(1e-6f));
            float area = 0f;
            for (int polygon = 0; polygon < mesh.PolygonCount; polygon++) area += mesh.GetPolygonArea(polygon);
            Assert.That(area, Is.EqualTo(39322.9f).Within(1f)); // 200 x 200 less the eroded rim
        }

        [Test]
        public void ParsesTheUndulatingMapWithSymmetricAdjacency()
        {
            DetourNavMesh mesh = Parse(Artifact("undulating"));
            Assert.That(mesh.PolygonCount, Is.EqualTo(232));
            Assert.That(mesh.VertexCount, Is.EqualTo(418));
            int shared = 0;
            for (int polygon = 0; polygon < mesh.PolygonCount; polygon++)
            {
                int corners = mesh.GetPolygonVertexCount(polygon);
                Assert.That(corners, Is.InRange(3, DetourNavMesh.MaxVerticesPerPolygon));
                for (int edge = 0; edge < corners; edge++)
                {
                    int neighbour = mesh.GetNeighbour(polygon, edge);
                    if (neighbour < 0) continue;
                    shared++;
                    bool backLink = false;
                    for (int back = 0; back < mesh.GetPolygonVertexCount(neighbour); back++)
                        backLink |= mesh.GetNeighbour(neighbour, back) == polygon;
                    Assert.That(backLink, Is.True, polygon + " -> " + neighbour + " has no way back");
                }
            }
            Assert.That(shared, Is.EqualTo(520));
        }

        [Test]
        public void RejectsArtifactsItCannotTrust()
        {
            byte[] good = Artifact("plane_200x200");
            byte[] Mutate(Action<byte[]> change)
            {
                byte[] copy = (byte[])good.Clone();
                change(copy);
                return copy;
            }

            AssertRejected(null, "shorter than its header");
            AssertRejected(new byte[40], "shorter than its header");
            AssertRejected(Mutate(b => b[0] = (byte)'X'), "NKMRCST1");
            AssertRejected(Mutate(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), 2)), "little-endian");
            byte[] truncated = new byte[good.Length - 4];
            Array.Copy(good, truncated, truncated.Length);
            AssertRejected(truncated, "payload size");
            AssertRejected(Mutate(b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(TileOffset + 4), 6)),
                "Detour version 7");
            // One more polygon than the bytes hold.
            AssertRejected(Mutate(b => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(TileOffset + 24), 66)),
                "sections add up");

            int polygonOffset = TileOffset + 100 + 12 * 128;
            AssertRejected(Mutate(b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(polygonOffset + 4), 500)),
                "names vertex 500");
            AssertRejected(Mutate(b => b[polygonOffset + 30] = 7), "has 7 vertices");
            AssertRejected(Mutate(b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(polygonOffset + 16), 0x8001)),
                "single-tile");
            AssertRejected(Mutate(b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(polygonOffset + 16), 200)),
                "names neighbour 199");
            AssertRejected(Mutate(b => b[polygonOffset + 31] = 1 << 6), "off-mesh");
            AssertRejected(Mutate(b => BinaryPrimitives.WriteInt32LittleEndian(
                b.AsSpan(TileOffset + 100), BitConverter.SingleToInt32Bits(float.NaN))), "not finite");
        }

        [Test]
        public void FindsThePolygonUnderAPointAndClampsPointsOffTheMesh()
        {
            DetourNavMeshQuery query = Plane();
            Assert.That(query.FindPolygon(Vector3.zero), Is.GreaterThanOrEqualTo(0));
            Assert.That(query.FindPolygon(new Vector3(150f, 0f, 0f)), Is.EqualTo(-1));

            Vector3 onMesh = query.ClosestPoint(new Vector3(0f, 5f, 0f), out int polygon);
            Assert.That(polygon, Is.GreaterThanOrEqualTo(0));
            Assert.That(Vector3.Distance(onMesh, new Vector3(0f, 0.2f, 0f)), Is.LessThan(1e-4f),
                "height comes from the mesh");

            Vector3 clamped = query.ClosestPoint(new Vector3(150f, 0f, 3f), out polygon);
            Assert.That(polygon, Is.GreaterThanOrEqualTo(0));
            Assert.That(clamped.x, Is.EqualTo(99.2f).Within(0.01f), "the eroded rim, not the 100 m bounds");
            Assert.That(clamped.z, Is.EqualTo(3f).Within(0.01f));
        }

        [Test]
        public void AcrossOpenGroundThePathIsAStraightLine()
        {
            DetourNavMeshQuery query = Plane();
            var corners = new List<Vector3>();
            Assert.That(query.FindPath(new Vector3(-90f, 0f, -90f), new Vector3(90f, 0f, 85f), corners), Is.True);
            Assert.That(corners.Count, Is.EqualTo(2), string.Join(" ", corners));
            Assert.That(Vector3.Distance(corners[0], new Vector3(-90f, 0.2f, -90f)), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(corners[1], new Vector3(90f, 0.2f, 85f)), Is.LessThan(1e-4f));
        }

        [Test]
        public void PathsBetweenRandomPointsStayOnTheMeshAndTurnWhereTheyMust()
        {
            DetourNavMeshQuery query = Undulating();
            var random = new System.Random(7);
            var corners = new List<Vector3>();
            int turning = 0;
            for (int trial = 0; trial < 50; trial++)
            {
                Vector3 start = query.RandomPoint(random), end = query.RandomPoint(random);
                if (!query.FindPath(start, end, corners)) continue; // separate islands are allowed
                Assert.That(corners[0].x, Is.EqualTo(start.x).Within(1e-4f));
                Assert.That(corners[corners.Count - 1].z, Is.EqualTo(end.z).Within(1e-4f));
                AssertWalkable(query, corners);
                if (corners.Count > 2) turning++;
            }
            Assert.That(turning, Is.GreaterThan(0), "this map has gaps a straight line cannot cross");
        }

        [Test]
        public void AnUnreachableEndIsReportedRatherThanGuessed()
        {
            DetourNavMeshQuery query = Undulating();
            DetourNavMesh mesh = query.Mesh;
            // Find two polygons with no route between them, if the map has islands.
            var island = new int[mesh.PolygonCount];
            for (int i = 0; i < island.Length; i++) island[i] = -1;
            int islands = 0;
            for (int seed = 0; seed < mesh.PolygonCount; seed++)
            {
                if (island[seed] >= 0) continue;
                var stack = new Stack<int>();
                stack.Push(seed);
                island[seed] = islands;
                while (stack.Count > 0)
                {
                    int polygon = stack.Pop();
                    for (int edge = 0; edge < mesh.GetPolygonVertexCount(polygon); edge++)
                    {
                        int next = mesh.GetNeighbour(polygon, edge);
                        if (next >= 0 && island[next] < 0)
                        {
                            island[next] = islands;
                            stack.Push(next);
                        }
                    }
                }
                islands++;
            }
            if (islands < 2) Assert.Ignore("undulating is one connected region");

            int other = Array.FindIndex(island, value => value != island[0]);
            var corners = new List<Vector3> { Vector3.one };
            Assert.That(query.FindPath(mesh.GetPolygonCenter(0), mesh.GetPolygonCenter(other), corners), Is.False);
            Assert.That(corners, Is.Empty);
        }

        [Test]
        public void RandomPointsAreWalkableAndRepeatableForASeed()
        {
            DetourNavMeshQuery query = Undulating();
            var first = new System.Random(42);
            var second = new System.Random(42);
            for (int sample = 0; sample < 500; sample++)
            {
                Vector3 point = query.RandomPoint(first);
                Assert.That(point, Is.EqualTo(query.RandomPoint(second)));
                Vector3 onMesh = query.ClosestPoint(point, out _);
                Assert.That(new Vector2(point.x - onMesh.x, point.z - onMesh.z).magnitude, Is.LessThan(0.01f));
            }
        }
    }
}
