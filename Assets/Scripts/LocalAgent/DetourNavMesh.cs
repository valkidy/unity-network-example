using System;
using System.Buffers.Binary;
using UnityEngine;

namespace NetworkExample.UnityDemo.LocalAgent
{
    /// <summary>
    /// The walkable polygons of one baked navigation mesh, read from the same
    /// <c>NKMRCST1</c> artifact the server's patrols path over.
    /// </summary>
    /// <remarks>
    /// The artifact is an 88-byte network-kernel header followed by one Detour
    /// (recastnavigation, version 7) tile. Only vertices and polygons are kept:
    /// links are rebuilt by Detour at run time and stored as zeros, the detail
    /// mesh only refines height (which the server decides), and the BV tree is an
    /// acceleration structure the few hundred polygons here do not need.
    /// Polygons are convex, with 3 to 6 vertices, and have already been eroded by
    /// the agent radius the mesh was baked for.
    /// </remarks>
    public sealed class DetourNavMesh
    {
        public const int MaxVerticesPerPolygon = 6;

        private const int ArtifactHeaderSize = 88;
        private const uint ArtifactSchemaVersion = 1;
        private const uint LittleEndianPayload = 1;
        private const int DetourMagic = 'D' << 24 | 'N' << 16 | 'A' << 8 | 'V';
        private const int DetourVersion = 7;
        private const int MeshHeaderSize = 100;
        private const int VertexSize = 12;
        private const int PolygonSize = 32;
        private const int LinkSize = 12;
        private const int DetailMeshSize = 12;
        private const int DetailVertexSize = 12;
        private const int DetailTriangleSize = 4;
        private const int BvNodeSize = 16;
        private const int OffMeshConnectionSize = 36;
        private const ushort ExternalLink = 0x8000;
        private const int OffMeshConnectionPolygonType = 1;
        private static readonly byte[] ArtifactMagic =
            { (byte)'N', (byte)'K', (byte)'M', (byte)'R', (byte)'C', (byte)'S', (byte)'T', (byte)'1' };

        private readonly Vector3[] vertices;
        private readonly int[][] polygonVertices;
        private readonly int[][] polygonNeighbours;
        private readonly Vector3[] polygonCenters;
        private readonly float[] polygonAreas;
        private readonly Vector4[] polygonBoundsXZ;

        private DetourNavMesh(Vector3[] vertices, int[][] polygonVertices, int[][] polygonNeighbours,
            Vector3 boundsMin, Vector3 boundsMax, float agentHeight, float agentRadius, float agentClimb)
        {
            this.vertices = vertices;
            this.polygonVertices = polygonVertices;
            this.polygonNeighbours = polygonNeighbours;
            BoundsMin = boundsMin;
            BoundsMax = boundsMax;
            AgentHeight = agentHeight;
            AgentRadius = agentRadius;
            AgentClimb = agentClimb;
            polygonCenters = new Vector3[polygonVertices.Length];
            polygonAreas = new float[polygonVertices.Length];
            polygonBoundsXZ = new Vector4[polygonVertices.Length];
            for (int polygon = 0; polygon < polygonVertices.Length; polygon++)
            {
                int[] indices = polygonVertices[polygon];
                Vector3 sum = Vector3.zero;
                var bounds = new Vector4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
                float twiceArea = 0f;
                for (int k = 0; k < indices.Length; k++)
                {
                    Vector3 a = vertices[indices[k]];
                    Vector3 b = vertices[indices[(k + 1) % indices.Length]];
                    sum += a;
                    twiceArea += a.x * b.z - b.x * a.z;
                    bounds = new Vector4(Mathf.Min(bounds.x, a.x), Mathf.Min(bounds.y, a.z),
                        Mathf.Max(bounds.z, a.x), Mathf.Max(bounds.w, a.z));
                }
                polygonCenters[polygon] = sum / indices.Length;
                polygonAreas[polygon] = Mathf.Abs(twiceArea) * 0.5f;
                polygonBoundsXZ[polygon] = bounds;
            }
        }

        public int PolygonCount => polygonVertices.Length;
        public int VertexCount => vertices.Length;
        public Vector3 BoundsMin { get; }
        public Vector3 BoundsMax { get; }
        /// <summary>The agent the mesh was baked for; walkable edges sit this far from walls.</summary>
        public float AgentRadius { get; }
        public float AgentHeight { get; }
        public float AgentClimb { get; }

        public Vector3 GetVertex(int index) => vertices[index];
        public int GetPolygonVertexCount(int polygon) => polygonVertices[polygon].Length;
        public Vector3 GetPolygonVertex(int polygon, int corner) =>
            vertices[polygonVertices[polygon][corner]];
        /// <summary>The polygon across edge <paramref name="edge"/> (corner to corner + 1), or -1 at a border.</summary>
        public int GetNeighbour(int polygon, int edge) => polygonNeighbours[polygon][edge];
        public Vector3 GetPolygonCenter(int polygon) => polygonCenters[polygon];
        /// <summary>Area projected on the ground plane, in square metres.</summary>
        public float GetPolygonArea(int polygon) => polygonAreas[polygon];
        /// <summary>(minX, minZ, maxX, maxZ).</summary>
        public Vector4 GetPolygonBoundsXZ(int polygon) => polygonBoundsXZ[polygon];

        public static bool TryParse(byte[] artifact, out DetourNavMesh mesh, out string diagnostic)
        {
            mesh = null;
            try
            {
                return TryParseChecked(artifact, out mesh, out diagnostic);
            }
            catch (ArgumentOutOfRangeException)
            {
                diagnostic = "Navigation mesh is truncated.";
                return false;
            }
        }

        private static bool TryParseChecked(byte[] artifact, out DetourNavMesh mesh, out string diagnostic)
        {
            mesh = null;
            if (artifact == null || artifact.Length < ArtifactHeaderSize)
            {
                diagnostic = "Navigation mesh is shorter than its header.";
                return false;
            }
            var span = new ReadOnlySpan<byte>(artifact);
            if (!span.Slice(0, ArtifactMagic.Length).SequenceEqual(ArtifactMagic))
            {
                diagnostic = "Navigation mesh does not start with NKMRCST1.";
                return false;
            }
            if (U32(span, 8) != ArtifactSchemaVersion || U32(span, 12) != ArtifactHeaderSize)
            {
                diagnostic = "Navigation mesh artifact schema " + U32(span, 8) + " is not supported.";
                return false;
            }
            if (U32(span, 24) != LittleEndianPayload)
            {
                diagnostic = "Navigation mesh payload is not little-endian.";
                return false;
            }
            ulong payloadSize = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(56));
            if (payloadSize != (ulong)(artifact.Length - ArtifactHeaderSize))
            {
                diagnostic = "Navigation mesh payload size " + payloadSize + " does not match the " +
                    (artifact.Length - ArtifactHeaderSize) + " bytes present.";
                return false;
            }

            ReadOnlySpan<byte> tile = span.Slice(ArtifactHeaderSize);
            if (tile.Length < MeshHeaderSize)
            {
                diagnostic = "Navigation mesh is truncated.";
                return false;
            }
            if (I32(tile, 0) != DetourMagic || I32(tile, 4) != DetourVersion)
            {
                diagnostic = "Navigation mesh is not a Detour version " + DetourVersion + " tile.";
                return false;
            }
            int polygonCount = I32(tile, 24);
            int vertexCount = I32(tile, 28);
            int linkCount = I32(tile, 32);
            int detailMeshCount = I32(tile, 36);
            int detailVertexCount = I32(tile, 40);
            int detailTriangleCount = I32(tile, 44);
            int bvNodeCount = I32(tile, 48);
            int offMeshConnectionCount = I32(tile, 52);
            if (polygonCount <= 0 || vertexCount <= 0 || linkCount < 0 || detailMeshCount < 0 ||
                detailVertexCount < 0 || detailTriangleCount < 0 || bvNodeCount < 0 ||
                offMeshConnectionCount < 0)
            {
                diagnostic = "Navigation mesh declares invalid section counts.";
                return false;
            }
            if (offMeshConnectionCount != 0)
            {
                diagnostic = "Navigation mesh off-mesh connections are not supported.";
                return false;
            }

            long vertexOffset = Align4(MeshHeaderSize);
            long polygonOffset = vertexOffset + Align4((long)VertexSize * vertexCount);
            long expected = polygonOffset + Align4((long)PolygonSize * polygonCount) +
                Align4((long)LinkSize * linkCount) + Align4((long)DetailMeshSize * detailMeshCount) +
                Align4((long)DetailVertexSize * detailVertexCount) +
                Align4((long)DetailTriangleSize * detailTriangleCount) +
                Align4((long)BvNodeSize * bvNodeCount) +
                Align4((long)OffMeshConnectionSize * offMeshConnectionCount);
            if (expected != tile.Length)
            {
                diagnostic = "Navigation mesh sections add up to " + expected + " bytes, not " +
                    tile.Length + ".";
                return false;
            }

            var vertices = new Vector3[vertexCount];
            for (int index = 0; index < vertexCount; index++)
            {
                int at = (int)vertexOffset + index * VertexSize;
                vertices[index] = new Vector3(F32(tile, at), F32(tile, at + 4), F32(tile, at + 8));
                if (!IsFinite(vertices[index]))
                {
                    diagnostic = "Navigation mesh vertex " + index + " is not finite.";
                    return false;
                }
            }

            var polygonVertices = new int[polygonCount][];
            var polygonNeighbours = new int[polygonCount][];
            for (int polygon = 0; polygon < polygonCount; polygon++)
            {
                int at = (int)polygonOffset + polygon * PolygonSize;
                int cornerCount = tile[at + 30];
                int type = tile[at + 31] >> 6;
                if (type == OffMeshConnectionPolygonType)
                {
                    diagnostic = "Navigation mesh off-mesh connections are not supported.";
                    return false;
                }
                if (cornerCount < 3 || cornerCount > MaxVerticesPerPolygon)
                {
                    diagnostic = "Navigation mesh polygon " + polygon + " has " + cornerCount + " vertices.";
                    return false;
                }
                var indices = new int[cornerCount];
                var neighbours = new int[cornerCount];
                for (int k = 0; k < cornerCount; k++)
                {
                    indices[k] = BinaryPrimitives.ReadUInt16LittleEndian(tile.Slice(at + 4 + 2 * k));
                    ushort neighbour = BinaryPrimitives.ReadUInt16LittleEndian(tile.Slice(at + 16 + 2 * k));
                    if (indices[k] >= vertexCount)
                    {
                        diagnostic = "Navigation mesh polygon " + polygon + " names vertex " + indices[k] + ".";
                        return false;
                    }
                    if ((neighbour & ExternalLink) != 0)
                    {
                        diagnostic = "Navigation mesh links to another tile; only single-tile meshes are supported.";
                        return false;
                    }
                    if (neighbour > polygonCount)
                    {
                        diagnostic = "Navigation mesh polygon " + polygon + " names neighbour " + (neighbour - 1) + ".";
                        return false;
                    }
                    neighbours[k] = neighbour - 1; // 0 is a border edge
                }
                polygonVertices[polygon] = indices;
                polygonNeighbours[polygon] = neighbours;
            }

            mesh = new DetourNavMesh(vertices, polygonVertices, polygonNeighbours,
                new Vector3(F32(tile, 72), F32(tile, 76), F32(tile, 80)),
                new Vector3(F32(tile, 84), F32(tile, 88), F32(tile, 92)),
                F32(tile, 60), F32(tile, 64), F32(tile, 68));
            diagnostic = null;
            return true;
        }

        private static long Align4(long size) => (size + 3) & ~3L;
        private static uint U32(ReadOnlySpan<byte> bytes, int at) =>
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(at));
        private static int I32(ReadOnlySpan<byte> bytes, int at) =>
            BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(at));
        private static float F32(ReadOnlySpan<byte> bytes, int at) =>
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(at)));
        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }
}
