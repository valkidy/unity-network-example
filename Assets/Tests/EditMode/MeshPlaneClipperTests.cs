using System.Collections.Generic;
using NetworkExample.UnityDemo.Shatter;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class MeshPlaneClipperTests
    {
        [Test]
        public void Clip_WithTheWholeMeshBehindThePlane_ChangesNothing()
        {
            ShatterGeometry cube = Cube(1f);

            ShatterGeometry kept = MeshPlaneClipper.Clip(
                cube,
                new Plane(Vector3.right, new Vector3(5f, 0f, 0f)),
                out int unclosed);

            Assert.That(kept, Is.SameAs(cube));
            Assert.That(unclosed, Is.Zero);
        }

        [Test]
        public void Clip_WithTheWholeMeshInFrontOfThePlane_KeepsNothing()
        {
            Assert.That(
                MeshPlaneClipper.Clip(
                    Cube(1f),
                    new Plane(Vector3.right, new Vector3(-5f, 0f, 0f)),
                    out _),
                Is.Null);
        }

        [Test]
        public void Clip_ThroughAClosedMesh_LeavesAClosedMesh()
        {
            ShatterGeometry half = MeshPlaneClipper.Clip(
                Cube(2f),
                new Plane(Vector3.right, Vector3.zero),
                out int unclosed);

            Assert.That(unclosed, Is.Zero);
            // The whole point of capping: a piece of a closed model is closed
            // again, so no shading ever looks into it.
            Assert.That(BoundaryEdges(half), Is.Zero);
        }

        [Test]
        public void Clip_KeepsOnlyWhatWasBehindThePlane()
        {
            ShatterGeometry half = MeshPlaneClipper.Clip(
                Cube(2f),
                new Plane(Vector3.right, Vector3.zero),
                out _);

            for (int index = 0; index < half.VertexCount; ++index)
            {
                Assert.That(half.Positions[index].x, Is.LessThanOrEqualTo(1e-4f));
            }

            // Still a metre of cube on the kept side, and the cut face closes it
            // exactly on the plane.
            Bounds bounds = Bounds(half);
            Assert.That(bounds.min.x, Is.EqualTo(-1f).Within(1e-4f));
            Assert.That(bounds.max.x, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(bounds.min.y, Is.EqualTo(-1f).Within(1e-4f));
            Assert.That(bounds.max.y, Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void Clip_WindsEveryFaceOutwards()
        {
            ShatterGeometry half = MeshPlaneClipper.Clip(
                Cube(2f),
                new Plane(new Vector3(1f, 1f, 0f).normalized, Vector3.zero),
                out _);

            // A convex piece has every face turned away from its middle. It is
            // the cap this is really asking about: wound the other way it would
            // be invisible from outside and the piece would look open.
            AssertEveryFaceFacesOutwards(half);
        }

        [Test]
        public void Fracture_CutsAPartIntoPiecesThatAreEachClosed()
        {
            MeshIsland part = CubeIsland(4f);

            List<MeshIsland> pieces = MeshIslandFracturer.Fracture(
                part,
                6,
                7,
                out int unclosed);

            Assert.That(pieces.Count, Is.GreaterThan(1));
            Assert.That(unclosed, Is.Zero);
            for (int index = 0; index < pieces.Count; ++index)
            {
                Assert.That(
                    BoundaryEdges(ShatterGeometry.From(pieces[index])),
                    Is.Zero,
                    "piece " + index + " is open");
                AssertEveryFaceFacesOutwards(ShatterGeometry.From(pieces[index]));
            }
        }

        [Test]
        public void Fracture_PiecesStayInsideThePartTheyCameFrom()
        {
            MeshIsland part = CubeIsland(4f);

            List<MeshIsland> pieces = MeshIslandFracturer.Fracture(part, 5, 3, out _);

            // Every piece knows where it stood, and together they fill the part
            // and nothing more: cells tile what they were cut from.
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int index = 0; index < pieces.Count; ++index)
            {
                MeshIsland piece = pieces[index];
                min = Vector3.Min(min, piece.Pivot + piece.LocalBounds.min);
                max = Vector3.Max(max, piece.Pivot + piece.LocalBounds.max);
            }

            Assert.That(Vector3.Distance(min, part.Pivot + part.LocalBounds.min),
                Is.LessThan(1e-3f));
            Assert.That(Vector3.Distance(max, part.Pivot + part.LocalBounds.max),
                Is.LessThan(1e-3f));
        }

        [Test]
        public void Fracture_FromTheSameSeed_CutsThePartTheSameWay()
        {
            MeshIsland part = CubeIsland(4f);

            List<MeshIsland> first = MeshIslandFracturer.Fracture(part, 5, 11, out _);
            List<MeshIsland> again = MeshIslandFracturer.Fracture(part, 5, 11, out _);
            List<MeshIsland> other = MeshIslandFracturer.Fracture(part, 5, 12, out _);

            Assert.That(again.Count, Is.EqualTo(first.Count));
            for (int index = 0; index < first.Count; ++index)
            {
                Assert.That(again[index].Pivot, Is.EqualTo(first[index].Pivot));
                Assert.That(again[index].TriangleCount, Is.EqualTo(first[index].TriangleCount));
            }

            Assert.That(
                Same(first, other),
                Is.False,
                "Another seed should cut the part another way.");
        }

        [Test]
        public void Fracture_LeavesThePartsThatAreAlreadySmallEnoughAlone()
        {
            var parts = new List<MeshIsland> { CubeIsland(1f), CubeIsland(4f) };

            List<MeshIsland> pieces = MeshIslandFracturer.Fracture(
                parts,
                3f,
                1.2f,
                1,
                out int partsCut,
                out _);

            Assert.That(partsCut, Is.EqualTo(1));
            Assert.That(pieces.Count, Is.GreaterThan(parts.Count));

            bool keptWhole = false;
            for (int index = 0; index < pieces.Count; ++index)
            {
                keptWhole |= ReferenceEquals(pieces[index], parts[0]);
            }

            Assert.That(keptWhole, Is.True, "The small part is passed through untouched.");
        }

        private static bool Same(List<MeshIsland> first, List<MeshIsland> other)
        {
            if (first.Count != other.Count)
            {
                return false;
            }

            for (int index = 0; index < first.Count; ++index)
            {
                if (Vector3.Distance(first[index].Pivot, other[index].Pivot) > 1e-4f)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Every face of a convex piece turned away from the inside of it.
        /// </summary>
        /// <remarks>
        /// The inside is taken as the average of the piece's own corners, which
        /// for a convex piece is a point in it -- the centre of its bounds is
        /// not, once a cell comes out as a thin diagonal wedge.
        /// </remarks>
        private static void AssertEveryFaceFacesOutwards(ShatterGeometry geometry)
        {
            Vector3 middle = Vector3.zero;
            for (int index = 0; index < geometry.VertexCount; ++index)
            {
                middle += geometry.Positions[index];
            }

            middle /= Mathf.Max(1, geometry.VertexCount);
            for (int start = 0; start + 2 < geometry.Triangles.Count; start += 3)
            {
                Vector3 a = geometry.Positions[geometry.Triangles[start]];
                Vector3 b = geometry.Positions[geometry.Triangles[start + 1]];
                Vector3 c = geometry.Positions[geometry.Triangles[start + 2]];
                Vector3 face = Vector3.Cross(b - a, c - a);
                if (face.sqrMagnitude < 1e-12f)
                {
                    continue;
                }

                Assert.That(
                    Vector3.Dot(face.normalized, ((a + b + c) / 3f) - middle),
                    Is.GreaterThan(-1e-3f),
                    "triangle at " + start + " faces inwards");
            }
        }

        /// <summary>
        /// Edges with one face on them, counted over positions welded within a
        /// tenth of a millimetre. Zero for a surface with no hole in it.
        /// </summary>
        /// <remarks>
        /// Welded by distance rather than exactly, because a cut computes the
        /// same point down two paths -- along the wall and along the cap a
        /// previous cut left there -- and floating point does not always agree
        /// with itself to the last bit. Anything closer than this is the same
        /// point, and an edge left with one face on it after that is a hole
        /// something could be seen through.
        ///
        /// Edges with more than two faces are not holes and are not counted: a
        /// cut through a place where the model already folded back on itself
        /// leaves surfaces meeting there, which draws as itself and not as a
        /// tear.
        /// </remarks>
        private static int BoundaryEdges(ShatterGeometry geometry)
        {
            var welded = new List<Vector3>();
            var ids = new int[geometry.VertexCount];
            for (int index = 0; index < ids.Length; ++index)
            {
                Vector3 position = geometry.Positions[index];
                ids[index] = -1;
                for (int other = 0; other < welded.Count; ++other)
                {
                    if (Vector3.Distance(welded[other], position) < 1e-4f)
                    {
                        ids[index] = other;
                        break;
                    }
                }

                if (ids[index] < 0)
                {
                    ids[index] = welded.Count;
                    welded.Add(position);
                }
            }

            var uses = new Dictionary<long, int>();
            for (int start = 0; start + 2 < geometry.Triangles.Count; start += 3)
            {
                int a = ids[geometry.Triangles[start]];
                int b = ids[geometry.Triangles[start + 1]];
                int c = ids[geometry.Triangles[start + 2]];
                Count(uses, a, b);
                Count(uses, b, c);
                Count(uses, c, a);
            }

            int boundary = 0;
            foreach (KeyValuePair<long, int> edge in uses)
            {
                bool isLoop = (int)(edge.Key >> 32) == (int)(edge.Key & 0xFFFFFFFF);
                if (edge.Value == 1 && !isLoop)
                {
                    ++boundary;
                }
            }

            return boundary;
        }

        private static void Count(Dictionary<long, int> uses, int from, int to)
        {
            long key = from < to
                ? ((long)from << 32) | (uint)to
                : ((long)to << 32) | (uint)from;
            uses[key] = uses.TryGetValue(key, out int used) ? used + 1 : 1;
        }

        private static Bounds Bounds(ShatterGeometry geometry)
        {
            var bounds = new Bounds(geometry.Positions[0], Vector3.zero);
            for (int index = 1; index < geometry.VertexCount; ++index)
            {
                bounds.Encapsulate(geometry.Positions[index]);
            }

            return bounds;
        }

        private static MeshIsland CubeIsland(float size)
        {
            return Cube(size).ToIsland(Vector3.zero);
        }

        /// <summary>
        /// A closed cube centred on the origin, <paramref name="size"/> across.
        /// </summary>
        private static ShatterGeometry Cube(float size)
        {
            var geometry = new ShatterGeometry(true, true);
            float half = size * 0.5f;
            for (int corner = 0; corner < 8; ++corner)
            {
                var position = new Vector3(
                    (corner & 1) == 0 ? -half : half,
                    (corner & 2) == 0 ? -half : half,
                    (corner & 4) == 0 ? -half : half);
                geometry.Add(position, position.normalized, new Vector2(position.x, position.y));
            }

            // Every face wound counter-clockwise seen from outside.
            AddQuad(geometry, 0, 2, 3, 1, Vector3.back);
            AddQuad(geometry, 4, 5, 7, 6, Vector3.forward);
            AddQuad(geometry, 0, 1, 5, 4, Vector3.down);
            AddQuad(geometry, 2, 6, 7, 3, Vector3.up);
            AddQuad(geometry, 0, 4, 6, 2, Vector3.left);
            AddQuad(geometry, 1, 3, 7, 5, Vector3.right);
            return geometry;
        }

        private static void AddQuad(
            ShatterGeometry geometry,
            int a,
            int b,
            int c,
            int d,
            Vector3 outwards)
        {
            geometry.AddTriangle(a, b, c);
            geometry.AddTriangle(a, c, d);
            Vector3 first = geometry.Positions[a];
            Vector3 second = geometry.Positions[b];
            Vector3 third = geometry.Positions[c];
            Assert.That(
                Vector3.Dot(Vector3.Cross(second - first, third - first).normalized, outwards),
                Is.GreaterThan(0.9f),
                "the test cube is wound inside out");
        }
    }
}
