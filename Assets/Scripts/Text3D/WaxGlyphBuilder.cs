using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>
    /// Builds one character's WaxCandy glyph: outline, mesh arrays and edge distance map.
    /// </summary>
    /// <remarks>
    /// Pure computation on managed arrays with no Unity object access, so it runs on any
    /// thread; <see cref="WaxGlyphAssets"/> turns the result into Unity objects on the
    /// main thread.
    ///
    /// Stages: flatten the font's quadratic contours, merge overlapping contours into
    /// one silhouette, round sharp corners, fit the triangle budget, extrude, and bake
    /// the distance map from the rounded silhouette.
    ///
    /// The budget is met by choosing how many wall bands to use. A mesh with N outline
    /// vertices and S bands has 2N(S + 1) triangles plus four per hole minus four per
    /// region, so the builder takes as many bands as fit, up to the maximum. Only when
    /// even the minimum band count does not fit is the outline simplified. The distance
    /// map is always baked from the unsimplified silhouette, so simplification moves the
    /// mesh edge off the map's zero line by at most the removed detail.
    /// </remarks>
    public static class WaxGlyphBuilder
    {
        private const double MinimumCornerTurn = 30.0 * Math.PI / 180.0;
        private const double DefaultCornerArcStepDegrees = 45.0;
        private const double CleanTolerance = 1e-7;
        private const int MaxMapSize = 4096;

        public static WaxGlyphGeometry Build(TrueTypeFont font, int codepoint, WaxGlyphSettings settings)
        {
            if (font == null)
            {
                throw new ArgumentNullException(nameof(font));
            }

            var stopwatch = Stopwatch.StartNew();
            var geometry = new WaxGlyphGeometry(codepoint)
            {
                DistanceRange = settings.mapDistanceRange,
                HalfDepth = settings.halfDepth,
                TriangulationClean = true,
            };

            int glyphIndex = font.GetGlyphIndex(codepoint);
            double scale = settings.capHeight / Math.Max(1, font.CapHeight);
            geometry.GlyphIndex = glyphIndex;
            geometry.Advance = (float)(font.GetAdvanceWidth(glyphIndex) * scale);

            var outline = new List<List<GlyphOutlinePoint>>();
            font.AppendOutline(glyphIndex, outline);
            var contours = new List<List<GlyphVector>>(outline.Count);
            foreach (List<GlyphOutlinePoint> contour in outline)
            {
                List<GlyphVector> loop = GlyphPolygon.Clean(
                    GlyphOutlineFlattener.Flatten(contour, scale, settings.curveTolerance),
                    CleanTolerance);
                if (loop.Count >= 3)
                {
                    contours.Add(loop);
                }
            }

            geometry.OutlineMilliseconds = Lap(stopwatch);
            if (contours.Count == 0)
            {
                return geometry;
            }

            // Components serialized before the setting existed read 0.
            double arcStepDegrees = settings.cornerArcStepDegrees > 0f ? settings.cornerArcStepDegrees : DefaultCornerArcStepDegrees;
            double arcStep = Math.Max(5.0, arcStepDegrees) * Math.PI / 180.0;
            var silhouette = new List<List<GlyphVector>>();
            foreach (List<GlyphVector> loop in GlyphPolygonUnion.Union(contours))
            {
                List<GlyphVector> rounded = GlyphPolygon.Clean(
                    GlyphPolygonShaping.RoundCorners(loop, settings.cornerRadius, MinimumCornerTurn, arcStep),
                    CleanTolerance);
                if (rounded.Count >= 3)
                {
                    silhouette.Add(rounded);
                }
            }

            geometry.UnionMilliseconds = Lap(stopwatch);
            if (silhouette.Count == 0)
            {
                return geometry;
            }

            int minSegments = Math.Max(1, settings.minWallSegments);
            int maxSegments = Math.Max(minSegments, settings.maxWallSegments);
            var meshLoops = silhouette.ConvertAll(loop => new List<GlyphVector>(loop));
            List<GlyphRegion> regions = GlyphPolygonShaping.BuildRegions(meshLoops);
            int segments = ChooseWallSegments(regions, settings.triangleBudget, minSegments, maxSegments, out bool fits);
            if (!fits)
            {
                GlyphPolygonShaping.SimplifyToVertexCount(
                    meshLoops,
                    MaxOutlineVertices(regions, settings.triangleBudget, minSegments));
                regions = GlyphPolygonShaping.BuildRegions(meshLoops);
                segments = ChooseWallSegments(regions, settings.triangleBudget, minSegments, maxSegments, out fits);
                geometry.Simplified = true;
            }

            geometry.OverBudget = !fits;
            geometry.SimplifyMilliseconds = Lap(stopwatch);

            GlyphPolygon.TryGetBounds(silhouette, out GlyphVector min, out GlyphVector max);
            double padding = Math.Max(settings.mapPadding, settings.wallBulge * 2.0);
            double coveredWidth = max.X - min.X + 2.0 * padding;
            double coveredHeight = max.Y - min.Y + 2.0 * padding;
            double texelSize = Math.Max(
                1.0 / Math.Max(1.0, settings.mapTexelsPerUnit),
                Math.Max(coveredWidth, coveredHeight) / MaxMapSize);
            int mapWidth = Math.Max(4, (int)Math.Ceiling(coveredWidth / texelSize));
            int mapHeight = Math.Max(4, (int)Math.Ceiling(coveredHeight / texelSize));
            double rectWidth = mapWidth * texelSize;
            double rectHeight = mapHeight * texelSize;
            double rectX = (min.X + max.X - rectWidth) * 0.5;
            double rectY = (min.Y + max.Y - rectHeight) * 0.5;

            GlyphExtruder.Extrude(regions, segments, settings, rectX, rectY, rectWidth, rectHeight, geometry);
            geometry.MapRect = new Vector4((float)rectX, (float)rectY, (float)rectWidth, (float)rectHeight);
            geometry.MeshRegions = regions;
            geometry.Silhouette = silhouette;
            geometry.MeshMilliseconds = Lap(stopwatch);

            geometry.DistanceMap = GlyphDistanceMap.Bake(
                silhouette,
                rectX,
                rectY,
                mapWidth,
                mapHeight,
                texelSize,
                settings.mapDistanceRange,
                out double maxDistance);
            geometry.MapWidth = mapWidth;
            geometry.MapHeight = mapHeight;
            geometry.MaxInteriorDistance = (float)maxDistance;
            geometry.MapMilliseconds = Lap(stopwatch);
            return geometry;
        }

        /// <summary>Triangles for the given regions extruded with <paramref name="segments"/> wall bands.</summary>
        public static int CountTriangles(IReadOnlyList<GlyphRegion> regions, int segments)
        {
            int caps = 0;
            int outline = 0;
            foreach (GlyphRegion region in regions)
            {
                caps += region.CapTriangleCount;
                outline += region.VertexCount;
            }

            return 2 * caps + 2 * outline * segments;
        }

        private static int ChooseWallSegments(
            IReadOnlyList<GlyphRegion> regions,
            int budget,
            int minSegments,
            int maxSegments,
            out bool fits)
        {
            int caps = 0;
            int outline = 0;
            foreach (GlyphRegion region in regions)
            {
                caps += region.CapTriangleCount;
                outline += region.VertexCount;
            }

            int affordable = outline > 0 ? (budget - 2 * caps) / (2 * outline) : maxSegments;
            fits = affordable >= minSegments;
            return Math.Max(minSegments, Math.Min(maxSegments, affordable));
        }

        // Largest outline that fits the budget at the minimum band count:
        // 2(N + 2H - 2R) + 2N * S <= budget.
        private static int MaxOutlineVertices(IReadOnlyList<GlyphRegion> regions, int budget, int minSegments)
        {
            int bridgeTerms = 0;
            foreach (GlyphRegion region in regions)
            {
                bridgeTerms += 2 * region.Holes.Count - 2;
            }

            return Math.Max(3, (budget - 2 * bridgeTerms) / (2 * (minSegments + 1)));
        }

        private static double Lap(Stopwatch stopwatch)
        {
            double milliseconds = stopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Restart();
            return milliseconds;
        }
    }
}
