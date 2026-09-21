using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace NetworkExample.UnityDemo.Shatter
{
    /// <summary>
    /// The OBJ files the bake exchanges with Tools/BlastSpike's blast_cut: one
    /// part written out for it to cut, and the pieces it writes back.
    /// </summary>
    /// <remarks>
    /// Kept apart from the code that starts the process so both directions can
    /// be tested without Blast installed. The format is blast_cut's own, not OBJ
    /// in general: one index per corner shared by position, normal and UV, and
    /// on the way back one group per piece with every face tagged
    /// <c>usemtl exterior</c> or <c>usemtl interior</c>.
    /// </remarks>
    public static class BlastObj
    {
        /// <summary>
        /// The part as blast_cut reads it, in the part's own space -- around its
        /// pivot -- so what comes back can be put back on the same pivot.
        /// </summary>
        public static string Write(MeshIsland part)
        {
            if (part == null)
            {
                throw new ArgumentNullException(nameof(part));
            }

            var text = new StringBuilder(part.VertexCount * 96);
            for (int index = 0; index < part.VertexCount; ++index)
            {
                Vector3 p = part.Positions[index];
                text.Append("v ").Append(F(p.x)).Append(' ').Append(F(p.y)).Append(' ').Append(F(p.z)).Append('\n');
            }

            for (int index = 0; index < part.VertexCount; ++index)
            {
                // blast_cut needs one of each per vertex; a part with no normals
                // hands it zeroes rather than nothing.
                Vector3 n = index < part.Normals.Length ? part.Normals[index] : Vector3.zero;
                text.Append("vn ").Append(F(n.x)).Append(' ').Append(F(n.y)).Append(' ').Append(F(n.z)).Append('\n');
            }

            for (int index = 0; index < part.VertexCount; ++index)
            {
                Vector2 uv = index < part.Uvs.Length ? part.Uvs[index] : Vector2.zero;
                text.Append("vt ").Append(F(uv.x)).Append(' ').Append(F(uv.y)).Append('\n');
            }

            int[] triangles = part.Triangles;
            for (int start = 0; start + 2 < triangles.Length; start += 3)
            {
                text.Append('f');
                for (int corner = 0; corner < 3; ++corner)
                {
                    int one = triangles[start + corner] + 1;
                    text.Append(' ').Append(one).Append('/').Append(one).Append('/').Append(one);
                }

                text.Append('\n');
            }

            return text.ToString();
        }

        /// <summary>
        /// The pieces blast_cut wrote back, each on its own pivot, with
        /// <paramref name="partPivot"/> -- where the part stood in the model --
        /// carried through.
        /// </summary>
        /// <remarks>
        /// blast_cut writes three fresh vertices per triangle. Corners that agree
        /// on position, normal and UV are welded back together here, which is
        /// most of them: a piece comes back at a fraction of the vertices, and
        /// nothing that should be a seam is closed, because a seam is exactly
        /// where the normal or the UV disagrees.
        /// </remarks>
        public static List<MeshIsland> Read(string text, Vector3 partPivot)
        {
            var pieces = new List<MeshIsland>();
            if (string.IsNullOrEmpty(text))
            {
                return pieces;
            }

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            PieceBuilder piece = null;
            bool interior = false;
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] parts = line.Split(' ');
                    switch (parts[0])
                    {
                        case "v":
                            positions.Add(ReadVector3(parts));
                            break;
                        case "vn":
                            normals.Add(ReadVector3(parts));
                            break;
                        case "vt":
                            uvs.Add(new Vector2(P(parts[1]), P(parts[2])));
                            break;
                        case "g":
                            FinishInto(pieces, piece, partPivot);
                            piece = new PieceBuilder();
                            interior = false;
                            break;
                        case "usemtl":
                            interior = parts.Length > 1 && parts[1] == "interior";
                            break;
                        case "f":
                            if (piece == null)
                            {
                                piece = new PieceBuilder();
                            }

                            for (int corner = 1; corner <= 3; ++corner)
                            {
                                int vertex = int.Parse(
                                    parts[corner].Split('/')[0],
                                    CultureInfo.InvariantCulture) - 1;
                                piece.AddCorner(
                                    positions[vertex],
                                    normals[vertex],
                                    uvs[vertex],
                                    interior);
                            }

                            break;
                    }
                }
            }

            FinishInto(pieces, piece, partPivot);
            return pieces;
        }

        private static void FinishInto(List<MeshIsland> pieces, PieceBuilder piece, Vector3 partPivot)
        {
            if (piece != null && piece.TriangleCount > 0)
            {
                pieces.Add(piece.ToIsland(partPivot));
            }
        }

        private static Vector3 ReadVector3(string[] parts)
        {
            return new Vector3(P(parts[1]), P(parts[2]), P(parts[3]));
        }

        private static float P(string value)
        {
            return float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static string F(float value)
        {
            // G9 round-trips a float exactly.
            return value.ToString("G9", CultureInfo.InvariantCulture);
        }

        private sealed class PieceBuilder
        {
            private readonly Dictionary<Corner, int> welded = new Dictionary<Corner, int>();
            private readonly List<Vector3> positions = new List<Vector3>();
            private readonly List<Vector3> normals = new List<Vector3>();
            private readonly List<Vector2> uvs = new List<Vector2>();
            private readonly List<int> surface = new List<int>();
            private readonly List<int> interior = new List<int>();

            public int TriangleCount => (surface.Count + interior.Count) / 3;

            public void AddCorner(Vector3 position, Vector3 normal, Vector2 uv, bool isInterior)
            {
                var corner = new Corner(position, normal, uv);
                if (!welded.TryGetValue(corner, out int index))
                {
                    index = positions.Count;
                    welded.Add(corner, index);
                    positions.Add(position);
                    normals.Add(normal);
                    uvs.Add(uv);
                }

                (isInterior ? interior : surface).Add(index);
            }

            public MeshIsland ToIsland(Vector3 partPivot)
            {
                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (int index = 0; index < positions.Count; ++index)
                {
                    min = Vector3.Min(min, positions[index]);
                    max = Vector3.Max(max, positions[index]);
                }

                Vector3 pivot = (min + max) * 0.5f;
                var centred = new Vector3[positions.Count];
                for (int index = 0; index < centred.Length; ++index)
                {
                    centred[index] = positions[index] - pivot;
                }

                return new MeshIsland(
                    centred,
                    normals.ToArray(),
                    uvs.ToArray(),
                    Array.Empty<Vector4>(),
                    surface.ToArray(),
                    interior.ToArray(),
                    partPivot + pivot,
                    new Bounds(Vector3.zero, max - min));
            }
        }

        private readonly struct Corner : IEquatable<Corner>
        {
            private readonly Vector3 position;
            private readonly Vector3 normal;
            private readonly Vector2 uv;

            public Corner(Vector3 position, Vector3 normal, Vector2 uv)
            {
                this.position = position;
                this.normal = normal;
                this.uv = uv;
            }

            public bool Equals(Corner other)
            {
                return position.Equals(other.position) &&
                    normal.Equals(other.normal) &&
                    uv.Equals(other.uv);
            }

            public override bool Equals(object obj)
            {
                return obj is Corner other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (position.GetHashCode() * 397) ^
                        (normal.GetHashCode() * 31) ^
                        uv.GetHashCode();
                }
            }
        }
    }
}
