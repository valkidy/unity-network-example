using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NetworkExample.UnityDemo.Text3D;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class WaxGlyphTests
    {
        private const string FontFileName = "ComicRelief-Bold.ttf";
        private const string DigitsAndLatinLetters = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        private static TrueTypeFont font;

        private static TrueTypeFont Font
        {
            get
            {
                if (font == null)
                {
                    string path = Path.Combine(Application.streamingAssetsPath, "Fonts", FontFileName);
                    Assert.That(File.Exists(path), Is.True, $"Test font missing at {path}.");
                    font = TrueTypeFont.Load(File.ReadAllBytes(path));
                }

                return font;
            }
        }

        [Test]
        public void Load_ComicReliefBold_ReadsMetricsAndCharacterMap()
        {
            Assert.That(Font.UnitsPerEm, Is.EqualTo(2048));
            Assert.That(Font.CapHeight, Is.EqualTo(1554));
            Assert.That(Font.GetGlyphIndex('0'), Is.EqualTo(18));
            Assert.That(Font.GetGlyphIndex('A'), Is.EqualTo(35));
            Assert.That(Font.GetGlyphIndex('z'), Is.EqualTo(92));
            Assert.That(Font.GetAdvanceWidth(35), Is.EqualTo(1498));
            Assert.That(Font.GetGlyphIndex('字'), Is.Zero, "The font has no CJK glyphs, so they map to the missing glyph.");
        }

        [TestCase('I', 1, 40)]
        [TestCase('A', 2, 34)]
        [TestCase('8', 3, 62)]
        [TestCase('g', 2, 72)]
        [TestCase('i', 2, 46)] // Composite: the stem and the dot are separate component glyphs.
        public void AppendOutline_ReadsEveryContourAndPoint(char character, int contourCount, int pointCount)
        {
            var contours = new List<List<GlyphOutlinePoint>>();
            Font.AppendOutline(Font.GetGlyphIndex(character), contours);

            int points = 0;
            foreach (List<GlyphOutlinePoint> contour in contours)
            {
                points += contour.Count;
            }

            Assert.That(contours.Count, Is.EqualTo(contourCount));
            Assert.That(points, Is.EqualTo(pointCount));
        }

        [TestCase('A', 1, 1)]
        [TestCase('B', 1, 2)]
        [TestCase('8', 1, 2)]
        [TestCase('g', 1, 1)]
        [TestCase('i', 2, 0)]
        public void Union_FontContours_KeepTheNonZeroFill(char character, int outerCount, int holeCount)
        {
            AssertUnionKeepsFill(FlattenedContours(character), outerCount, holeCount, character.ToString());
        }

        [Test]
        public void Union_OverlappingContours_MergeIntoOneSilhouette()
        {
            // Comic Relief draws no glyph from overlapping pieces, so overlaps are built here:
            // four bars crossing at the corners make one frame around one hole.
            var contours = new List<List<GlyphVector>>
            {
                Rectangle(0.0, 0.0, 1.0, 0.25),
                Rectangle(0.0, 0.75, 1.0, 1.0),
                Rectangle(0.0, 0.0, 0.25, 1.0),
                Rectangle(0.75, 0.0, 1.0, 1.0),
            };

            List<List<GlyphVector>> union = AssertUnionKeepsFill(contours, 1, 1, "frame");

            double area = 0.0;
            foreach (List<GlyphVector> loop in union)
            {
                area += GlyphPolygon.SignedArea(loop);
            }

            Assert.That(area, Is.EqualTo(0.75).Within(1e-9), "frame area: the unit square minus the half-unit hole");
        }

        [Test]
        public void Build_EveryDigitAndLatinLetter_IsAClosedMeshWithinBudget()
        {
            WaxGlyphSettings settings = WaxGlyphSettings.Default;
            foreach (char character in DigitsAndLatinLetters)
            {
                WaxGlyphGeometry geometry = WaxGlyphBuilder.Build(Font, character, settings);
                string label = $"'{character}'";

                Assert.That(geometry.IsEmpty, Is.False, label);
                Assert.That(geometry.TriangleCount, Is.LessThanOrEqualTo(settings.triangleBudget), label);
                Assert.That(geometry.TriangulationClean, Is.True, $"{label} triangulation");
                Assert.That(geometry.WallSegments, Is.InRange(settings.minWallSegments, settings.maxWallSegments), label);
                Assert.That(geometry.Positions.Length, Is.EqualTo(geometry.OutlineVertexCount * (geometry.WallSegments + 1)), label);
                AssertClosedAndOutward(geometry, label);
                AssertCapsCoverSilhouette(geometry, label);
                AssertPlanarUvsAndBounds(geometry, label);
            }
        }

        [Test]
        public void Build_TightBudget_SimplifiesTheOutlineToFit()
        {
            WaxGlyphSettings settings = WaxGlyphSettings.Default;
            settings.triangleBudget = 400;

            WaxGlyphGeometry geometry = WaxGlyphBuilder.Build(Font, 'g', settings);

            Assert.That(geometry.Simplified, Is.True);
            Assert.That(geometry.OverBudget, Is.False);
            Assert.That(geometry.TriangleCount, Is.LessThanOrEqualTo(400));
            Assert.That(geometry.TriangulationClean, Is.True);
            AssertClosedAndOutward(geometry, "'g'");
            AssertCapsCoverSilhouette(geometry, "'g'");
        }

        [Test]
        public void Build_Space_HasAnAdvanceButNoMesh()
        {
            WaxGlyphGeometry geometry = WaxGlyphBuilder.Build(Font, ' ', WaxGlyphSettings.Default);

            Assert.That(geometry.IsEmpty, Is.True);
            Assert.That(geometry.Advance, Is.GreaterThan(0f));
        }

        [Test]
        public void DistanceMap_IsZeroOnTheSilhouetteAndRisesOneUnitPerUnit()
        {
            WaxGlyphSettings settings = WaxGlyphSettings.Default;
            WaxGlyphGeometry geometry = WaxGlyphBuilder.Build(Font, '|', settings);
            double texel = geometry.MapRect.z / geometry.MapWidth;

            Assert.That(geometry.MapRect.w / geometry.MapHeight, Is.EqualTo(texel).Within(1e-5), "square texels");
            Assert.That(geometry.DistanceMap[0], Is.EqualTo(0), "outside the glyph");

            // Front-rim vertices sit on the silhouette, where the shader must read no distance.
            for (int i = 0; i < geometry.OutlineVertexCount; i++)
            {
                double distance = SampleBilinear(geometry, geometry.Uvs[i]) * geometry.DistanceRange;
                Assert.That(distance, Is.LessThanOrEqualTo(1.5 * texel), $"rim vertex {i}");
            }

            // Across the bar at mid height the value climbs one texel per texel.
            int row = geometry.MapHeight / 2;
            int firstInside = 0;
            while (geometry.DistanceMap[row * geometry.MapWidth + firstInside] == 0)
            {
                firstInside++;
            }

            for (int k = 1; k <= 6; k++)
            {
                double step = (geometry.DistanceMap[row * geometry.MapWidth + firstInside + k] -
                               geometry.DistanceMap[row * geometry.MapWidth + firstInside + k - 1]) /
                              (double)ushort.MaxValue * geometry.DistanceRange;
                Assert.That(step, Is.EqualTo(texel).Within(texel * 0.02), $"step {k}");
            }

            // "|" is a straight bar 260 units wide at its widest (x 312 to 572 at the top), so the
            // deepest point is half of that.
            double halfStroke = 130.0 * settings.capHeight / Font.CapHeight;
            Assert.That(geometry.MaxInteriorDistance, Is.EqualTo(halfStroke).Within(texel));
        }

        [UnityTest]
        public IEnumerator DistanceMap_RunsBurstCompiled()
        {
            // The Editor compiles Burst code in the background after scripts reload and runs
            // plain C# until it is ready, so wait for it rather than reading the first answer.
            var waited = Stopwatch.StartNew();
            while (!GlyphDistanceMap.UsesBurst && waited.Elapsed.TotalSeconds < 120.0)
            {
                yield return null;
            }

            Assert.That(GlyphDistanceMap.UsesBurst, Is.True,
                "The distance map still ran as plain C# after two minutes; check that Jobs > Burst > Enable Compilation is on.");
        }

        [Test]
        public void Build_DigitsAndLatinLetters_LogsStageTimings()
        {
            WaxGlyphSettings settings = WaxGlyphSettings.Default;
            WaxGlyphBuilder.Build(Font, 'W', settings);

            var total = Stopwatch.StartNew();
            double outline = 0.0, union = 0.0, fit = 0.0, mesh = 0.0, map = 0.0, slowest = 0.0;
            char slowestCharacter = ' ';
            int maxTriangles = 0;
            long texels = 0;
            foreach (char character in DigitsAndLatinLetters)
            {
                WaxGlyphGeometry geometry = WaxGlyphBuilder.Build(Font, character, settings);
                outline += geometry.OutlineMilliseconds;
                union += geometry.UnionMilliseconds;
                fit += geometry.SimplifyMilliseconds;
                mesh += geometry.MeshMilliseconds;
                map += geometry.MapMilliseconds;
                maxTriangles = Math.Max(maxTriangles, geometry.TriangleCount);
                texels += (long)geometry.MapWidth * geometry.MapHeight;
                if (geometry.TotalMilliseconds > slowest)
                {
                    slowest = geometry.TotalMilliseconds;
                    slowestCharacter = character;
                }
            }

            int count = DigitsAndLatinLetters.Length;
            Debug.Log(
                $"WaxGlyph build of {count} glyphs (Burst {GlyphDistanceMap.UsesBurst}): {total.Elapsed.TotalMilliseconds:0.0} ms total, " +
                $"{total.Elapsed.TotalMilliseconds / count:0.00} ms each, slowest '{slowestCharacter}' {slowest:0.00} ms. " +
                $"Stages: outline {outline:0.0}, union {union:0.0}, fit {fit:0.0}, mesh {mesh:0.0}, map {map:0.0} ms. " +
                $"Max triangles {maxTriangles}, mean map {texels / count} texels.");
        }

        private static List<List<GlyphVector>> FlattenedContours(char character)
        {
            WaxGlyphSettings settings = WaxGlyphSettings.Default;
            double scale = settings.capHeight / Font.CapHeight;
            var outline = new List<List<GlyphOutlinePoint>>();
            Font.AppendOutline(Font.GetGlyphIndex(character), outline);
            var contours = new List<List<GlyphVector>>();
            foreach (List<GlyphOutlinePoint> contour in outline)
            {
                contours.Add(GlyphPolygon.Clean(GlyphOutlineFlattener.Flatten(contour, scale, settings.curveTolerance), 1e-7));
            }

            return contours;
        }

        private static List<GlyphVector> Rectangle(double minX, double minY, double maxX, double maxY) =>
            new List<GlyphVector>
            {
                new GlyphVector(minX, minY),
                new GlyphVector(maxX, minY),
                new GlyphVector(maxX, maxY),
                new GlyphVector(minX, maxY),
            };

        // Checks the union's loop counts, that its loops never cross, and that its even-odd
        // fill matches the contours' non-zero fill on a grid of points away from the edges.
        private static List<List<GlyphVector>> AssertUnionKeepsFill(
            List<List<GlyphVector>> contours,
            int outerCount,
            int holeCount,
            string label)
        {
            List<List<GlyphVector>> union = GlyphPolygonUnion.Union(contours);

            int outers = 0;
            int holes = 0;
            foreach (List<GlyphVector> loop in union)
            {
                if (GlyphPolygon.SignedArea(loop) > 0.0)
                {
                    outers++;
                }
                else
                {
                    holes++;
                }
            }

            Assert.That(outers, Is.EqualTo(outerCount), $"{label} outer loops");
            Assert.That(holes, Is.EqualTo(holeCount), $"{label} holes");
            AssertNoCrossings(union, label);

            GlyphPolygon.TryGetBounds(contours, out GlyphVector min, out GlyphVector max);
            for (int y = 0; y < 60; y++)
            {
                for (int x = 0; x < 60; x++)
                {
                    var point = new GlyphVector(min.X + (max.X - min.X) * (x + 0.5) / 60.0, min.Y + (max.Y - min.Y) * (y + 0.5) / 60.0);
                    if (DistanceToLoops(contours, point) < 0.002)
                    {
                        continue;
                    }

                    bool expected = GlyphPolygon.WindingNumber(contours, point) != 0;
                    int containing = 0;
                    foreach (List<GlyphVector> loop in union)
                    {
                        containing += GlyphPolygon.ContainsEvenOdd(loop, point) ? 1 : 0;
                    }

                    Assert.That(containing % 2 == 1, Is.EqualTo(expected), $"{label} fill at {point}");
                }
            }

            return union;
        }

        private static void AssertClosedAndOutward(WaxGlyphGeometry geometry, string label)
        {
            int[] triangles = geometry.Triangles;
            Vector3[] positions = geometry.Positions;
            var directedEdges = new Dictionary<long, int>();
            double volume = 0.0;
            for (int t = 0; t < triangles.Length; t += 3)
            {
                int a = triangles[t];
                int b = triangles[t + 1];
                int c = triangles[t + 2];
                AddEdge(directedEdges, a, b);
                AddEdge(directedEdges, b, c);
                AddEdge(directedEdges, c, a);
                volume += Vector3.Dot(positions[a], Vector3.Cross(positions[b], positions[c])) / 6.0;
            }

            foreach (KeyValuePair<long, int> edge in directedEdges)
            {
                int from = (int)(edge.Key >> 32);
                int to = (int)(edge.Key & 0xFFFFFFFF);
                Assert.That(edge.Value, Is.EqualTo(1), $"{label} edge {from}->{to} is used more than once");
                Assert.That(directedEdges.ContainsKey(((long)to << 32) | (uint)from), Is.True,
                    $"{label} edge {from}->{to} has no opposite: the mesh is open");
            }

            double capArea = CapArea(geometry);
            double expectedVolume = capArea * 2.0 * geometry.HalfDepth;
            Assert.That(volume, Is.EqualTo(expectedVolume).Within(expectedVolume * 0.1), $"{label} volume (positive means outward)");

            for (int t = 0; t < geometry.CapTriangleCount * 3; t += 3)
            {
                Vector3 normal = Vector3.Cross(positions[triangles[t + 1]] - positions[triangles[t]], positions[triangles[t + 2]] - positions[triangles[t]]);
                Assert.That(normal.z, Is.GreaterThanOrEqualTo(-1e-6f), $"{label} front cap triangle {t / 3} faces +Z");
            }
        }

        private static void AssertCapsCoverSilhouette(WaxGlyphGeometry geometry, string label)
        {
            double outlineArea = 0.0;
            foreach (GlyphRegion region in geometry.MeshRegions)
            {
                outlineArea += GlyphPolygon.SignedArea(region.Outer);
                foreach (List<GlyphVector> hole in region.Holes)
                {
                    outlineArea += GlyphPolygon.SignedArea(hole);
                }
            }

            Assert.That(CapArea(geometry), Is.EqualTo(outlineArea).Within(Math.Abs(outlineArea) * 1e-4), $"{label} cap area");
        }

        private static double CapArea(WaxGlyphGeometry geometry)
        {
            double area = 0.0;
            for (int t = 0; t < geometry.CapTriangleCount * 3; t += 3)
            {
                Vector3 a = geometry.Positions[geometry.Triangles[t]];
                Vector3 b = geometry.Positions[geometry.Triangles[t + 1]];
                Vector3 c = geometry.Positions[geometry.Triangles[t + 2]];
                area += 0.5 * ((double)(b.x - a.x) * (c.y - a.y) - (double)(b.y - a.y) * (c.x - a.x));
            }

            return area;
        }

        private static void AssertPlanarUvsAndBounds(WaxGlyphGeometry geometry, string label)
        {
            Vector4 rect = geometry.MapRect;
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < geometry.Positions.Length; i++)
            {
                Vector3 position = geometry.Positions[i];
                Assert.That(geometry.Uvs[i].x, Is.EqualTo((position.x - rect.x) / rect.z).Within(1e-4f), $"{label} u {i}");
                Assert.That(geometry.Uvs[i].y, Is.EqualTo((position.y - rect.y) / rect.w).Within(1e-4f), $"{label} v {i}");
                Assert.That(geometry.Normals[i].magnitude, Is.EqualTo(1f).Within(1e-3f), $"{label} normal {i}");
                min = Vector2.Min(min, position);
                max = Vector2.Max(max, position);
            }

            Assert.That(geometry.LetterBounds, Is.EqualTo(new Vector4(min.x, min.y, max.x, max.y)), $"{label} letter bounds");
            Assert.That(min.x >= rect.x && min.y >= rect.y && max.x <= rect.x + rect.z && max.y <= rect.y + rect.w, Is.True,
                $"{label} mesh lies inside the map");
        }

        private static void AddEdge(Dictionary<long, int> edges, int from, int to)
        {
            long key = ((long)from << 32) | (uint)to;
            edges.TryGetValue(key, out int count);
            edges[key] = count + 1;
        }

        private static void AssertNoCrossings(List<List<GlyphVector>> loops, string label)
        {
            for (int l1 = 0; l1 < loops.Count; l1++)
            {
                for (int l2 = l1; l2 < loops.Count; l2++)
                {
                    List<GlyphVector> first = loops[l1];
                    List<GlyphVector> second = loops[l2];
                    for (int i = 0; i < first.Count; i++)
                    {
                        for (int j = l1 == l2 ? i + 2 : 0; j < second.Count; j++)
                        {
                            if (l1 == l2 && i == 0 && j == second.Count - 1)
                            {
                                continue;
                            }

                            Assert.That(
                                GlyphPolygon.SegmentsIntersect(first[i], first[(i + 1) % first.Count], second[j], second[(j + 1) % second.Count]),
                                Is.False,
                                $"{label} loop {l1} edge {i} crosses loop {l2} edge {j}");
                        }
                    }
                }
            }
        }

        private static double DistanceToLoops(List<List<GlyphVector>> loops, GlyphVector point)
        {
            double best = double.PositiveInfinity;
            foreach (List<GlyphVector> loop in loops)
            {
                for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
                {
                    best = Math.Min(best, GlyphPolygon.DistanceToSegmentSquared(point, loop[j], loop[i]));
                }
            }

            return Math.Sqrt(best);
        }

        private static double SampleBilinear(WaxGlyphGeometry geometry, Vector2 uv)
        {
            double x = uv.x * geometry.MapWidth - 0.5;
            double y = uv.y * geometry.MapHeight - 0.5;
            int x0 = (int)Math.Floor(x);
            int y0 = (int)Math.Floor(y);
            double fx = x - x0;
            double fy = y - y0;

            double Texel(int tx, int ty)
            {
                tx = Math.Max(0, Math.Min(geometry.MapWidth - 1, tx));
                ty = Math.Max(0, Math.Min(geometry.MapHeight - 1, ty));
                return geometry.DistanceMap[ty * geometry.MapWidth + tx] / (double)ushort.MaxValue;
            }

            double bottom = Texel(x0, y0) * (1.0 - fx) + Texel(x0 + 1, y0) * fx;
            double top = Texel(x0, y0 + 1) * (1.0 - fx) + Texel(x0 + 1, y0 + 1) * fx;
            return bottom * (1.0 - fy) + top * fy;
        }
    }
}
