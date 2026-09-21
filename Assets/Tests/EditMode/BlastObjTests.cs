using System.Collections.Generic;
using NetworkExample.UnityDemo.Shatter;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class BlastObjTests
    {
        // Two pieces the way blast_cut writes them: three fresh vertices per
        // triangle, one group per piece, faces tagged by where they came from.
        private const string TwoPieces =
            "mtllib blast_cut.mtl\n" +
            "g chunk_0\n" +
            "usemtl exterior\n" +
            "v 0 0 0\nvn 0 0 1\nvt 0 0\n" +
            "v 1 0 0\nvn 0 0 1\nvt 1 0\n" +
            "v 0 1 0\nvn 0 0 1\nvt 0 1\n" +
            "f 1/1/1 2/2/2 3/3/3\n" +
            "v 1 0 0\nvn 0 0 1\nvt 1 0\n" +
            "v 1 1 0\nvn 0 0 1\nvt 1 1\n" +
            "v 0 1 0\nvn 0 0 1\nvt 0 1\n" +
            "f 4/4/4 5/5/5 6/6/6\n" +
            "usemtl interior\n" +
            "v 0 0 0\nvn 0 0 -1\nvt 0 0\n" +
            "v 0 1 0\nvn 0 0 -1\nvt 0 1\n" +
            "v 1 0 0\nvn 0 0 -1\nvt 1 0\n" +
            "f 7/7/7 8/8/8 9/9/9\n" +
            "g chunk_1\n" +
            "usemtl exterior\n" +
            "v 4 0 0\nvn 0 1 0\nvt 0 0\n" +
            "v 5 0 0\nvn 0 1 0\nvt 1 0\n" +
            "v 4 0 2\nvn 0 1 0\nvt 0 1\n" +
            "f 10/10/10 11/11/11 12/12/12\n";

        [Test]
        public void Read_GivesOnePiecePerGroup_WithTheCutFacesKeptApart()
        {
            List<MeshIsland> pieces = BlastObj.Read(TwoPieces, Vector3.zero);

            Assert.That(pieces.Count, Is.EqualTo(2));
            Assert.That(pieces[0].Triangles.Length / 3, Is.EqualTo(2));
            Assert.That(pieces[0].InteriorTriangles.Length / 3, Is.EqualTo(1));
            Assert.That(pieces[1].InteriorTriangles, Is.Empty);
        }

        [Test]
        public void Read_WeldsCornersThatAgree_AndKeepsSeamsApart()
        {
            MeshIsland piece = BlastObj.Read(TwoPieces, Vector3.zero)[0];

            // Nine corners in: the two surface triangles share two corners, and
            // the cut face repeats three positions with a different normal --
            // which is a seam, and has to stay one.
            Assert.That(piece.VertexCount, Is.EqualTo(4 + 3));
        }

        [Test]
        public void Read_PutsEachPieceOnItsOwnPivot_WithinThePart()
        {
            var partPivot = new Vector3(10f, 20f, 30f);

            List<MeshIsland> pieces = BlastObj.Read(TwoPieces, partPivot);

            Assert.That(pieces[0].Pivot, Is.EqualTo(partPivot + new Vector3(0.5f, 0.5f, 0f)));
            Assert.That(pieces[1].Pivot, Is.EqualTo(partPivot + new Vector3(4.5f, 0f, 1f)));
            Assert.That(pieces[1].LocalBounds.size, Is.EqualTo(new Vector3(1f, 0f, 2f)));
        }

        [Test]
        public void WriteThenRead_GivesBackThePart()
        {
            // A piece with no cut faces: blast_cut is only ever handed a part
            // before anything has cut it, so Write has no interior to write.
            MeshIsland part = BlastObj.Read(TwoPieces, new Vector3(3f, 0f, 0f))[1];

            MeshIsland back = BlastObj.Read(BlastObj.Write(part), part.Pivot)[0];

            Assert.That(back.TriangleCount, Is.EqualTo(part.TriangleCount));
            Assert.That(back.Pivot, Is.EqualTo(part.Pivot));
            Assert.That(back.LocalBounds.size, Is.EqualTo(part.LocalBounds.size));
        }
    }
}
