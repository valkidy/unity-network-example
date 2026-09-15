using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>
    /// Extrudes glyph regions into the mesh layout of the WaxCandy reference letter.
    /// </summary>
    /// <remarks>
    /// Every outline vertex gets one ring per wall band boundary. Ring 0 is the front
    /// rim at +Z and ring <c>segments</c> the back rim; both double as cap vertices and
    /// carry flat ±Z normals. The rings between follow a half ellipse: they bulge a
    /// little past the silhouette at mid depth, with z spaced by the sine of evenly
    /// spaced angles so bands crowd toward the rims where the normals turn fastest.
    ///
    /// Nothing is split: caps and walls share the rim vertices, which is what V7's
    /// smooth-normal bump expects and keeps the mesh closed.
    /// </remarks>
    internal static class GlyphExtruder
    {
        private const double MinimumMiterDot = 0.35;

        public static void Extrude(
            List<GlyphRegion> regions,
            int segments,
            WaxGlyphSettings settings,
            double rectX,
            double rectY,
            double rectWidth,
            double rectHeight,
            WaxGlyphGeometry geometry)
        {
            var loops = new List<List<GlyphVector>>();
            var regionLoopStarts = new List<int[]>(regions.Count);
            int ringSize = 0;
            int holeCount = 0;
            foreach (GlyphRegion region in regions)
            {
                var starts = new int[region.Holes.Count + 1];
                starts[0] = ringSize;
                loops.Add(region.Outer);
                ringSize += region.Outer.Count;
                for (int h = 0; h < region.Holes.Count; h++)
                {
                    starts[h + 1] = ringSize;
                    loops.Add(region.Holes[h]);
                    ringSize += region.Holes[h].Count;
                }

                holeCount += region.Holes.Count;
                regionLoopStarts.Add(starts);
            }

            int ringCount = segments + 1;
            var positions = new Vector3[ringSize * ringCount];
            var normals = new Vector3[positions.Length];
            var uvs = new Vector2[positions.Length];
            double halfDepth = settings.halfDepth;
            double bulge = settings.wallBulge;
            double roundness = settings.wallNormalRoundness;

            int loopStart = 0;
            foreach (List<GlyphVector> loop in loops)
            {
                int count = loop.Count;
                for (int i = 0; i < count; i++)
                {
                    GlyphVector previous = loop[(i + count - 1) % count];
                    GlyphVector current = loop[i];
                    GlyphVector next = loop[(i + 1) % count];

                    // The solid is on the left of every loop, so the right normal points out of it.
                    GlyphVector outgoingNormal = (next - current).Normalized().RightNormal;
                    GlyphVector outward = ((current - previous).Normalized().RightNormal + outgoingNormal).Normalized();
                    if (outward.LengthSquared == 0.0)
                    {
                        outward = outgoingNormal;
                    }

                    double miter = 1.0 / Math.Max(GlyphVector.Dot(outward, outgoingNormal), MinimumMiterDot);
                    for (int ring = 0; ring < ringCount; ring++)
                    {
                        bool rim = ring == 0 || ring == segments;
                        double angle = 0.5 * Math.PI - Math.PI * ring / segments;
                        double sine = Math.Sin(angle);
                        double cosine = rim ? 0.0 : Math.Cos(angle);
                        GlyphVector planar = current + outward * (bulge * cosine * miter);
                        int vertex = ring * ringSize + loopStart + i;
                        positions[vertex] = new Vector3((float)planar.X, (float)planar.Y, (float)(halfDepth * sine));
                        uvs[vertex] = new Vector2(
                            (float)((planar.X - rectX) / rectWidth),
                            (float)((planar.Y - rectY) / rectHeight));

                        if (ring == 0)
                        {
                            normals[vertex] = Vector3.forward;
                        }
                        else if (ring == segments)
                        {
                            normals[vertex] = Vector3.back;
                        }
                        else
                        {
                            // Normal of the ellipse (roundness * cos, halfDepth * sin).
                            double horizontal = roundness > 0.0 ? cosine / roundness : 1.0;
                            double vertical = roundness > 0.0 && halfDepth > 0.0 ? sine / halfDepth : 0.0;
                            normals[vertex] = new Vector3(
                                (float)(outward.X * horizontal),
                                (float)(outward.Y * horizontal),
                                (float)vertical).normalized;
                        }
                    }
                }

                loopStart += count;
            }

            var capTriangles = new List<int>();
            bool clean = true;
            for (int r = 0; r < regions.Count; r++)
            {
                clean &= GlyphTriangulator.Triangulate(regions[r], regionLoopStarts[r], capTriangles);
            }

            int wallTriangles = ringSize * segments * 2;
            var triangles = new int[capTriangles.Count * 2 + wallTriangles * 3];
            int write = 0;

            // Counter-clockwise in XY faces +Z in Unity, so the front cap keeps the order
            // and the back cap reverses it.
            foreach (int index in capTriangles)
            {
                triangles[write++] = index;
            }

            int backRing = segments * ringSize;
            for (int t = 0; t < capTriangles.Count; t += 3)
            {
                triangles[write++] = capTriangles[t] + backRing;
                triangles[write++] = capTriangles[t + 2] + backRing;
                triangles[write++] = capTriangles[t + 1] + backRing;
            }

            loopStart = 0;
            foreach (List<GlyphVector> loop in loops)
            {
                int count = loop.Count;
                for (int i = 0; i < count; i++)
                {
                    int j = (i + 1) % count;
                    for (int ring = 0; ring < segments; ring++)
                    {
                        int upperI = ring * ringSize + loopStart + i;
                        int upperJ = ring * ringSize + loopStart + j;
                        int lowerI = upperI + ringSize;
                        int lowerJ = upperJ + ringSize;
                        triangles[write++] = upperI;
                        triangles[write++] = lowerI;
                        triangles[write++] = upperJ;
                        triangles[write++] = upperJ;
                        triangles[write++] = lowerI;
                        triangles[write++] = lowerJ;
                    }
                }

                loopStart += count;
            }

            float minX = float.PositiveInfinity;
            float minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float maxY = float.NegativeInfinity;
            foreach (Vector3 position in positions)
            {
                minX = Mathf.Min(minX, position.x);
                minY = Mathf.Min(minY, position.y);
                maxX = Mathf.Max(maxX, position.x);
                maxY = Mathf.Max(maxY, position.y);
            }

            geometry.Positions = positions;
            geometry.Normals = normals;
            geometry.Uvs = uvs;
            geometry.Triangles = triangles;
            geometry.CapTriangleCount = capTriangles.Count / 3;
            geometry.WallSegments = segments;
            geometry.OutlineVertexCount = ringSize;
            geometry.RegionCount = regions.Count;
            geometry.HoleCount = holeCount;
            geometry.TriangulationClean = clean;
            geometry.LetterBounds = new Vector4(minX, minY, maxX, maxY);
        }
    }
}
