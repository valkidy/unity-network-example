using System.Collections.Generic;
using NetworkExample.UnityDemo.Shatter;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class MeshIslandSplitterTests
    {
        [Test]
        public void Split_QuadsThatShareNothing_ComeBackAsOnePieceEach()
        {
            var positions = new List<Vector3>();
            var triangles = new List<int>();
            AddQuad(positions, triangles, new Vector3(0f, 0f, 0f), 1f);
            AddQuad(positions, triangles, new Vector3(10f, 0f, 0f), 1f);

            List<MeshIsland> islands = Split(positions, triangles);

            Assert.That(islands.Count, Is.EqualTo(2));
            Assert.That(islands[0].TriangleCount, Is.EqualTo(2));
            Assert.That(islands[1].TriangleCount, Is.EqualTo(2));
            Assert.That(islands[0].VertexCount, Is.EqualTo(4));
            Assert.That(islands[1].VertexCount, Is.EqualTo(4));
        }

        [Test]
        public void Split_Piece_IsCentredOnItsPivot()
        {
            var positions = new List<Vector3>();
            var triangles = new List<int>();
            AddQuad(positions, triangles, new Vector3(10f, 4f, -2f), 2f);

            MeshIsland island = Split(positions, triangles)[0];

            Assert.That(island.Pivot, Is.EqualTo(new Vector3(11f, 5f, -2f)));
            Assert.That(island.LocalBounds.center, Is.EqualTo(Vector3.zero));
            Assert.That(island.LocalBounds.size, Is.EqualTo(new Vector3(2f, 2f, 0f)));
            for (int index = 0; index < island.Positions.Length; ++index)
            {
                Assert.That(island.Positions[index] + island.Pivot,
                    Is.EqualTo(positions[index]),
                    "A chunk put back on its pivot stands where the model drew it.");
            }
        }

        [Test]
        public void Split_VerticesDuplicatedForASeam_StayOnePiece()
        {
            // What an exporter leaves behind: two triangles that share an edge,
            // written with their own copies of the two vertices on it because the
            // UVs jump across the seam. Welding by position is what puts them
            // back together.
            var positions = new List<Vector3>
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f),
                new Vector3(1f, 1f, 0f),
            };
            var uvs = new Vector2[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
                new Vector2(0f, 0f),
                new Vector2(1f, 1f),
                new Vector2(1f, 0f),
            };
            var triangles = new List<int> { 0, 1, 2, 3, 5, 4 };

            List<MeshIsland> islands = MeshIslandSplitter.Split(
                positions.ToArray(),
                null,
                uvs,
                null,
                triangles.ToArray());

            Assert.That(islands.Count, Is.EqualTo(1));
            Assert.That(islands[0].TriangleCount, Is.EqualTo(2));
            Assert.That(islands[0].VertexCount, Is.EqualTo(6),
                "The seam is welded to trace the piece, not to collapse it: the " +
                "duplicates carry the UVs that made the exporter split them.");
            Assert.That(islands[0].Uvs.Length, Is.EqualTo(6));
        }

        [Test]
        public void Split_QuadsMeetingAtNegativeZero_StayOnePiece()
        {
            var positions = new List<Vector3>
            {
                new Vector3(-1f, 0f, 0f),
                new Vector3(-0.0f, 0f, 0f),
                new Vector3(-1f, 1f, 0f),
                new Vector3(-0.0f, 1f, 0f),
                new Vector3(0.0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0.0f, 1f, 0f),
                new Vector3(1f, 1f, 0f),
            };
            var triangles = new List<int> { 0, 1, 2, 2, 1, 3, 4, 5, 6, 6, 5, 7 };

            List<MeshIsland> islands = Split(positions, triangles);

            Assert.That(islands.Count, Is.EqualTo(1),
                "Zero and negative zero are the same place, whatever they hash to.");
        }

        [Test]
        public void Split_Pieces_ComeBackLargestFirst()
        {
            var positions = new List<Vector3>();
            var triangles = new List<int>();
            AddQuad(positions, triangles, new Vector3(0f, 0f, 0f), 1f);
            AddTriangle(positions, triangles, new Vector3(10f, 0f, 0f), 1f);
            AddQuad(positions, triangles, new Vector3(20f, 0f, 0f), 1f);

            List<MeshIsland> islands = Split(positions, triangles);

            Assert.That(islands.Count, Is.EqualTo(3));
            Assert.That(islands[0].TriangleCount, Is.EqualTo(2));
            Assert.That(islands[1].TriangleCount, Is.EqualTo(2));
            Assert.That(islands[2].TriangleCount, Is.EqualTo(1));
            Assert.That(islands[0].Pivot.x, Is.LessThan(islands[1].Pivot.x),
                "Pieces of the same size keep the order they appear in.");
        }

        [Test]
        public void Split_NothingToSplit_ReturnsNothing()
        {
            Assert.That(
                MeshIslandSplitter.Split(new Vector3[0], null, null, null, new int[0]),
                Is.Empty);
        }

        [Test]
        public void Split_EveryTriangle_BelongsToExactlyOnePiece()
        {
            var positions = new List<Vector3>();
            var triangles = new List<int>();
            for (int index = 0; index < 7; ++index)
            {
                AddQuad(positions, triangles, new Vector3(index * 5f, 0f, 0f), 1f);
            }

            List<MeshIsland> islands = Split(positions, triangles);

            int total = 0;
            for (int index = 0; index < islands.Count; ++index)
            {
                total += islands[index].TriangleCount;
            }

            Assert.That(islands.Count, Is.EqualTo(7));
            Assert.That(total, Is.EqualTo(triangles.Count / 3));
        }

        private static List<MeshIsland> Split(List<Vector3> positions, List<int> triangles)
        {
            return MeshIslandSplitter.Split(
                positions.ToArray(),
                null,
                null,
                null,
                triangles.ToArray());
        }

        private static void AddQuad(
            List<Vector3> positions,
            List<int> triangles,
            Vector3 origin,
            float size)
        {
            int first = positions.Count;
            positions.Add(origin);
            positions.Add(origin + new Vector3(size, 0f, 0f));
            positions.Add(origin + new Vector3(0f, size, 0f));
            positions.Add(origin + new Vector3(size, size, 0f));
            triangles.AddRange(new[] { first, first + 1, first + 2 });
            triangles.AddRange(new[] { first + 2, first + 1, first + 3 });
        }

        private static void AddTriangle(
            List<Vector3> positions,
            List<int> triangles,
            Vector3 origin,
            float size)
        {
            int first = positions.Count;
            positions.Add(origin);
            positions.Add(origin + new Vector3(size, 0f, 0f));
            positions.Add(origin + new Vector3(0f, size, 0f));
            triangles.AddRange(new[] { first, first + 1, first + 2 });
        }
    }
}
