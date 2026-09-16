using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkExample.UnityDemo.Shatter
{
    /// <summary>
    /// Splits a mesh into the pieces it is already made of: groups of triangles
    /// that share vertices, with no triangle in one group touching a triangle in
    /// another.
    /// </summary>
    /// <remarks>
    /// This is the cheap half of shattering something. A model built out of parts
    /// -- a tower of beams, planks and a roof -- carries those parts in one mesh
    /// as separate connected pieces, and pulling them apart needs no cutting at
    /// all: every piece is whole, closed wherever the source was closed, and
    /// already carries its own UVs into the shared atlas. What it does not give
    /// is a piece smaller than the parts the artist modelled, so a single long
    /// beam comes out as one long beam. Cutting those down is a separate pass.
    ///
    /// Vertices are welded by exact position before pieces are traced, because an
    /// exporter splits a vertex wherever the UV or the normal jumps -- a seam, a
    /// hard edge -- and those duplicates would otherwise read as a break in the
    /// surface. Exact, not within a tolerance: the two sides of a seam were one
    /// vertex before the exporter split them, so they hold the same coordinates
    /// bit for bit, and a tolerance instead starts welding parts that merely
    /// touch. On the tower model, welding at a millimetre already fuses two
    /// pieces that only rest against each other.
    ///
    /// Winding, materials and submeshes are left alone: every piece keeps the
    /// triangles it had, and a source with more than one submesh comes back as
    /// pieces that no longer know which submesh they came from. Splitting a
    /// multi-material model is therefore the caller's problem to notice; the
    /// tower has one material for the whole thing.
    /// </remarks>
    public static class MeshIslandSplitter
    {
        /// <summary>
        /// Splits <paramref name="mesh"/>, largest piece first.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The mesh holds vertices but will not hand them over, which is what a
        /// mesh imported without read/write access looks like from here.
        /// </exception>
        public static List<MeshIsland> Split(Mesh mesh)
        {
            if (mesh == null)
            {
                throw new ArgumentNullException(nameof(mesh));
            }

            Vector3[] positions = mesh.vertices;
            if (positions.Length == 0 && mesh.vertexCount > 0)
            {
                throw new InvalidOperationException(
                    "Mesh '" + mesh.name + "' reports " + mesh.vertexCount +
                    " vertices but returned none. Enable read/write on the model " +
                    "it was imported from.");
            }

            return Split(positions, mesh.normals, mesh.uv, mesh.tangents, mesh.triangles);
        }

        /// <summary>
        /// Splits raw mesh arrays, largest piece first. Pieces of equal size keep
        /// the order their first triangle appears in <paramref name="triangles"/>,
        /// so the same input always produces the same pieces in the same order.
        /// </summary>
        public static List<MeshIsland> Split(
            Vector3[] positions,
            Vector3[] normals,
            Vector2[] uvs,
            Vector4[] tangents,
            int[] triangles)
        {
            if (positions == null)
            {
                throw new ArgumentNullException(nameof(positions));
            }

            if (triangles == null)
            {
                throw new ArgumentNullException(nameof(triangles));
            }

            var islands = new List<MeshIsland>();
            if (triangles.Length < 3)
            {
                return islands;
            }

            int[] weldIds = WeldPositions(positions, out int weldedCount);
            var parents = new int[weldedCount];
            for (int index = 0; index < weldedCount; ++index)
            {
                parents[index] = index;
            }

            for (int start = 0; start + 2 < triangles.Length; start += 3)
            {
                int a = weldIds[triangles[start]];
                int b = weldIds[triangles[start + 1]];
                int c = weldIds[triangles[start + 2]];
                Union(parents, a, b);
                Union(parents, b, c);
            }

            var islandOfRoot = new Dictionary<int, int>();
            var trianglesPerIsland = new List<List<int>>();
            for (int start = 0; start + 2 < triangles.Length; start += 3)
            {
                int root = Find(parents, weldIds[triangles[start]]);
                if (!islandOfRoot.TryGetValue(root, out int island))
                {
                    island = trianglesPerIsland.Count;
                    islandOfRoot.Add(root, island);
                    trianglesPerIsland.Add(new List<int>());
                }

                trianglesPerIsland[island].Add(start);
            }

            // One map for every piece, cleared only where it was written. Sizing
            // it per piece instead would walk the whole vertex buffer once per
            // piece, which on a hundred pieces is the split's whole cost.
            var sourceToIsland = new int[positions.Length];
            for (int index = 0; index < sourceToIsland.Length; ++index)
            {
                sourceToIsland[index] = -1;
            }

            for (int island = 0; island < trianglesPerIsland.Count; ++island)
            {
                islands.Add(
                    BuildIsland(
                        positions,
                        normals,
                        uvs,
                        tangents,
                        triangles,
                        trianglesPerIsland[island],
                        sourceToIsland));
            }

            SortLargestFirst(islands);
            return islands;
        }

        private static MeshIsland BuildIsland(
            Vector3[] positions,
            Vector3[] normals,
            Vector2[] uvs,
            Vector4[] tangents,
            int[] triangles,
            List<int> triangleStarts,
            int[] sourceToIsland)
        {
            bool hasNormals = normals != null && normals.Length == positions.Length;
            bool hasUvs = uvs != null && uvs.Length == positions.Length;
            bool hasTangents = tangents != null && tangents.Length == positions.Length;

            var sourceIndices = new List<int>();
            var islandTriangles = new int[triangleStarts.Count * 3];
            for (int triangle = 0; triangle < triangleStarts.Count; ++triangle)
            {
                int start = triangleStarts[triangle];
                for (int corner = 0; corner < 3; ++corner)
                {
                    int sourceIndex = triangles[start + corner];
                    int mapped = sourceToIsland[sourceIndex];
                    if (mapped < 0)
                    {
                        mapped = sourceIndices.Count;
                        sourceToIsland[sourceIndex] = mapped;
                        sourceIndices.Add(sourceIndex);
                    }

                    islandTriangles[(triangle * 3) + corner] = mapped;
                }
            }

            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int index = 0; index < sourceIndices.Count; ++index)
            {
                Vector3 position = positions[sourceIndices[index]];
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }

            Vector3 pivot = (min + max) * 0.5f;
            var islandPositions = new Vector3[sourceIndices.Count];
            var islandNormals = hasNormals ? new Vector3[sourceIndices.Count] : Array.Empty<Vector3>();
            var islandUvs = hasUvs ? new Vector2[sourceIndices.Count] : Array.Empty<Vector2>();
            var islandTangents = hasTangents ? new Vector4[sourceIndices.Count] : Array.Empty<Vector4>();
            for (int index = 0; index < sourceIndices.Count; ++index)
            {
                int sourceIndex = sourceIndices[index];
                islandPositions[index] = positions[sourceIndex] - pivot;
                if (hasNormals)
                {
                    islandNormals[index] = normals[sourceIndex];
                }

                if (hasUvs)
                {
                    islandUvs[index] = uvs[sourceIndex];
                }

                if (hasTangents)
                {
                    islandTangents[index] = tangents[sourceIndex];
                }

                sourceToIsland[sourceIndex] = -1;
            }

            return new MeshIsland(
                islandPositions,
                islandNormals,
                islandUvs,
                islandTangents,
                islandTriangles,
                pivot,
                new Bounds(Vector3.zero, max - min));
        }

        /// <summary>
        /// Largest piece first. Pieces of the same size keep the order they are
        /// already in, so a list sorted twice does not change.
        /// </summary>
        public static void SortLargestFirst(List<MeshIsland> islands)
        {
            var order = new int[islands.Count];
            for (int index = 0; index < order.Length; ++index)
            {
                order[index] = index;
            }

            MeshIsland[] created = islands.ToArray();
            Array.Sort(
                order,
                (left, right) =>
                {
                    int bySize = created[right].TriangleCount.CompareTo(
                        created[left].TriangleCount);
                    return bySize != 0 ? bySize : left.CompareTo(right);
                });

            for (int index = 0; index < order.Length; ++index)
            {
                islands[index] = created[order[index]];
            }
        }

        /// <summary>
        /// Maps every vertex to an id shared by every vertex standing in exactly
        /// the same place.
        /// </summary>
        private static int[] WeldPositions(Vector3[] positions, out int weldedCount)
        {
            var ids = new Dictionary<Vector3, int>(positions.Length);
            var weldIds = new int[positions.Length];
            for (int index = 0; index < positions.Length; ++index)
            {
                Vector3 key = NormalizeZeroes(positions[index]);
                if (!ids.TryGetValue(key, out int id))
                {
                    id = ids.Count;
                    ids.Add(key, id);
                }

                weldIds[index] = id;
            }

            weldedCount = ids.Count;
            return weldIds;
        }

        /// <summary>
        /// Folds negative zero onto zero.
        /// </summary>
        /// <remarks>
        /// The two compare equal, so the weld wants them to be one vertex, but a
        /// dictionary consults the hash first and runtimes have disagreed over
        /// whether the sign of zero belongs in it. A model is full of exact
        /// zeroes wherever it was modelled on an axis, and folding the sign here
        /// costs a comparison and takes the question out.
        /// </remarks>
        private static Vector3 NormalizeZeroes(Vector3 position)
        {
            return new Vector3(
                position.x == 0f ? 0f : position.x,
                position.y == 0f ? 0f : position.y,
                position.z == 0f ? 0f : position.z);
        }

        private static int Find(int[] parents, int node)
        {
            while (parents[node] != node)
            {
                parents[node] = parents[parents[node]];
                node = parents[node];
            }

            return node;
        }

        private static void Union(int[] parents, int left, int right)
        {
            int leftRoot = Find(parents, left);
            int rightRoot = Find(parents, right);
            if (leftRoot != rightRoot)
            {
                parents[leftRoot] = rightRoot;
            }
        }
    }
}
