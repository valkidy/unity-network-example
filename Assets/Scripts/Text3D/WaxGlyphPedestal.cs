using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>
    /// How the melted-wax pedestal under a glyph is shaped and colored.
    /// </summary>
    /// <remarks>
    /// Lengths are glyph object units, like <see cref="WaxGlyphSettings"/>, so a pedestal
    /// scales with its glyph.
    /// </remarks>
    [Serializable]
    public struct WaxGlyphPedestalSettings
    {
        [Tooltip("Height above the glyph's bottom whose outline counts as standing on the pedestal.")]
        public float contactBand;

        [Tooltip("Narrowest contact; a round bottom that barely touches still gets a pool this wide.")]
        public float minimumContactWidth;

        [Tooltip("How far the wax pools out around each contact.")]
        public float spread;

        [Tooltip("How far the thinner wax joining separate contacts pools out, so a glyph with two feet stands in one pool.")]
        public float bridgeSpread;

        [Tooltip("Half the pedestal's thickness. Written to its material's _HalfDepth.")]
        public float halfThickness;

        [Tooltip("Height the glyph's bottom sits above the ground, sunk into the pedestal.")]
        public float glyphLift;

        [Tooltip("How far the poured lobes push the rim out.")]
        public float lobeAmplitude;

        [Tooltip("Lobes around the rim per unit of its length.")]
        public float lobesPerUnit;

        [Tooltip("Spacing the rim is resampled at before the lobes are added.")]
        public float resampleSpacing;

        [Tooltip("Triangle ceiling for the pedestal, caps and walls together.")]
        public int triangleBudget;

        [Tooltip("Most wall bands around the rim.")]
        public int maxWallSegments;

        [Tooltip("How far the middle of the rim bulges out.")]
        public float wallBulge;

        [Tooltip("Horizontal half-axis of the ellipse the rim normals follow; larger reads rounder.")]
        public float wallNormalRoundness;

        [Tooltip("Edge distance map resolution in texels per object unit.")]
        public float mapTexelsPerUnit;

        [Tooltip("Empty border around the pedestal in the edge distance map.")]
        public float mapPadding;

        [Tooltip("Distance stored at full intensity. Written to the material's _DistanceRange.")]
        public float mapDistanceRange;

        [Tooltip("How far into the glyph's layers the rim lands; 1 reaches the body color just inside the outline.")]
        public float colorReach;

        [Tooltip("Bends the spread: above 1 the glyph's inner layers hold longer and the outer ones crowd toward the rim.")]
        public float colorCurve;

        /// <remarks>
        /// Tuned against Comic Relief Bold letters and digits: every one pools into a single
        /// region of 1,400 to 2,800 triangles, and a curve of 3 keeps the glyph's pink under it
        /// with a thin cyan ring inside the orange rim.
        /// </remarks>
        public static WaxGlyphPedestalSettings Default => new WaxGlyphPedestalSettings
        {
            contactBand = 0.12f,
            minimumContactWidth = 0.2f,
            spread = 0.26f,
            bridgeSpread = 0.14f,
            halfThickness = 0.07f,
            glyphLift = 0.063f,
            lobeAmplitude = 0.14f,
            lobesPerUnit = 1.4f,
            resampleSpacing = 0.03f,
            triangleBudget = 4000,
            maxWallSegments = 6,
            wallBulge = 0.03f,
            wallNormalRoundness = 0.06f,
            mapTexelsPerUnit = 128f,
            mapPadding = 0.06f,
            mapDistanceRange = 0.5f,
            colorReach = 1f,
            colorCurve = 3f,
        };
    }

    /// <summary>
    /// Builds the pool of melted wax a glyph stands in: mesh arrays and edge distance map.
    /// </summary>
    /// <remarks>
    /// The footprint is where the glyph touches the ground: the silhouette's x spans in a thin
    /// band above its bottom, each spanning the glyph's depth, grown by a rounded spread and
    /// joined by a thinner bridge. Its outline is resampled and pushed out by a wave periodic
    /// in the rim's length, so the rim reads as poured lobes, then extruded like a glyph and laid
    /// flat: the outline plane becomes the ground plane (x, -z) and the extrusion depth becomes
    /// the height, with the bottom on y = 0.
    ///
    /// The result keeps the glyph's layout in map space: UVs, <see cref="WaxGlyphGeometry.MapRect"/>
    /// and <see cref="WaxGlyphGeometry.LetterBounds"/> describe the footprint plane, so WaxCandy V7
    /// renders it with <c>_FrontAxis</c> set to +Y. The contact spans it returns are what the
    /// material needs to spread the glyph's own layers outward from where it stands.
    ///
    /// Pure computation on managed arrays, like <see cref="WaxGlyphBuilder"/>, so it runs on any thread.
    /// </remarks>
    public static class WaxGlyphPedestal
    {
        private const double CleanTolerance = 1e-7;
        private const int ContactSamples = 4;
        private const int CornerSteps = 8;
        private const int MinimumWallSegments = 2;
        private const int MaxMapSize = 2048;

        /// <param name="glyph">A built glyph with an outline; its x axis is shared with the pedestal.</param>
        /// <param name="seed">Picks the lobe pattern; the same seed builds the same pedestal.</param>
        /// <param name="contactSpans">Where the glyph stands, as (x start, x end), sorted and not overlapping.</param>
        public static WaxGlyphGeometry Build(
            WaxGlyphGeometry glyph,
            WaxGlyphPedestalSettings settings,
            int seed,
            out List<Vector2> contactSpans)
        {
            if (glyph == null || glyph.IsEmpty || glyph.Silhouette == null)
            {
                throw new ArgumentException("A pedestal needs a glyph with an outline.", nameof(glyph));
            }

            var stopwatch = Stopwatch.StartNew();
            var geometry = new WaxGlyphGeometry(glyph.Codepoint)
            {
                GlyphIndex = glyph.GlyphIndex,
                DistanceRange = settings.mapDistanceRange,
                HalfDepth = settings.halfThickness,
                TriangulationClean = true,
            };

            List<(double start, double end)> contacts = ContactIntervals(glyph.Silhouette, settings);
            contactSpans = contacts.ConvertAll(span => new Vector2((float)span.start, (float)span.end));
            var blobs = new List<IReadOnlyList<GlyphVector>>();
            foreach ((double start, double end) contact in contacts)
            {
                blobs.Add(RoundedRectangle(contact.start, contact.end, glyph.HalfDepth, settings.spread));
            }

            if (contacts.Count > 1)
            {
                blobs.Add(RoundedRectangle(contacts[0].start, contacts[contacts.Count - 1].end, glyph.HalfDepth * 0.6, settings.bridgeSpread));
            }

            var random = new System.Random(seed);
            var lobed = new List<IReadOnlyList<GlyphVector>>();
            foreach (List<GlyphVector> loop in OuterLoops(GlyphPolygonUnion.Union(blobs)))
            {
                lobed.Add(AddLobes(Resample(loop, settings.resampleSpacing), settings, random));
            }

            // A second union resolves any place where lobes pushed the rim across itself.
            List<List<GlyphVector>> silhouette = OuterLoops(GlyphPolygonUnion.Union(lobed));
            geometry.OutlineMilliseconds = Lap(stopwatch);
            if (silhouette.Count == 0)
            {
                return geometry;
            }

            var meshLoops = silhouette.ConvertAll(loop => new List<GlyphVector>(loop));
            List<GlyphRegion> regions = GlyphPolygonShaping.BuildRegions(meshLoops);
            int segments = ChooseWallSegments(regions, settings);
            if (WaxGlyphBuilder.CountTriangles(regions, segments) > settings.triangleBudget)
            {
                GlyphPolygonShaping.SimplifyToVertexCount(
                    meshLoops,
                    Math.Max(12, settings.triangleBudget / (2 * (segments + 1))));
                regions = GlyphPolygonShaping.BuildRegions(meshLoops);
                segments = ChooseWallSegments(regions, settings);
                geometry.Simplified = true;
            }

            geometry.OverBudget = WaxGlyphBuilder.CountTriangles(regions, segments) > settings.triangleBudget;
            geometry.SimplifyMilliseconds = Lap(stopwatch);

            WaxGlyphSettings meshSettings = WaxGlyphSettings.Default;
            meshSettings.halfDepth = settings.halfThickness;
            meshSettings.wallBulge = settings.wallBulge;
            meshSettings.wallNormalRoundness = settings.wallNormalRoundness;
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

            GlyphExtruder.Extrude(regions, segments, meshSettings, rectX, rectY, rectWidth, rectHeight, geometry);
            geometry.MapRect = new Vector4((float)rectX, (float)rectY, (float)rectWidth, (float)rectHeight);
            geometry.MeshRegions = regions;
            geometry.Silhouette = silhouette;
            LayFlat(geometry, settings.halfThickness);
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

        private static int ChooseWallSegments(IReadOnlyList<GlyphRegion> regions, WaxGlyphPedestalSettings settings)
        {
            int segments = Math.Max(MinimumWallSegments, settings.maxWallSegments);
            while (segments > MinimumWallSegments && WaxGlyphBuilder.CountTriangles(regions, segments) > settings.triangleBudget)
            {
                segments--;
            }

            return segments;
        }

        // x spans of the silhouette in a thin band above its bottom, widened and merged.
        private static List<(double start, double end)> ContactIntervals(
            IReadOnlyList<List<GlyphVector>> silhouette,
            WaxGlyphPedestalSettings settings)
        {
            GlyphPolygon.TryGetBounds(silhouette, out GlyphVector min, out _);
            var spans = new List<(double start, double end)>();
            var crossings = new List<double>();
            for (int sample = 0; sample < ContactSamples; sample++)
            {
                double height = min.Y + settings.contactBand * (sample + 0.5) / ContactSamples;
                crossings.Clear();
                foreach (List<GlyphVector> loop in silhouette)
                {
                    for (int i = 0; i < loop.Count; i++)
                    {
                        GlyphVector a = loop[i];
                        GlyphVector b = loop[(i + 1) % loop.Count];
                        if ((a.Y <= height) != (b.Y <= height))
                        {
                            crossings.Add(a.X + (height - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                        }
                    }
                }

                // The union leaves no overlaps, so crossings pair up into inside spans.
                crossings.Sort();
                for (int i = 0; i + 1 < crossings.Count; i += 2)
                {
                    double center = 0.5 * (crossings[i] + crossings[i + 1]);
                    double half = Math.Max(0.5 * (crossings[i + 1] - crossings[i]), 0.5 * settings.minimumContactWidth);
                    spans.Add((center - half, center + half));
                }
            }

            spans.Sort((a, b) => a.start.CompareTo(b.start));
            var merged = new List<(double start, double end)>();
            foreach ((double start, double end) span in spans)
            {
                if (merged.Count > 0 && span.start <= merged[merged.Count - 1].end)
                {
                    (double start, double end) last = merged[merged.Count - 1];
                    merged[merged.Count - 1] = (last.start, Math.Max(last.end, span.end));
                }
                else
                {
                    merged.Add(span);
                }
            }

            return merged;
        }

        // Counter-clockwise rectangle [x0, x1] by [-halfDepth, halfDepth], grown by radius.
        private static List<GlyphVector> RoundedRectangle(double x0, double x1, double halfDepth, double radius)
        {
            var loop = new List<GlyphVector>(4 * (CornerSteps + 1));
            AddQuarterArc(loop, x1, -halfDepth, radius, -0.5 * Math.PI);
            AddQuarterArc(loop, x1, halfDepth, radius, 0.0);
            AddQuarterArc(loop, x0, halfDepth, radius, 0.5 * Math.PI);
            AddQuarterArc(loop, x0, -halfDepth, radius, Math.PI);
            return loop;
        }

        private static void AddQuarterArc(List<GlyphVector> loop, double x, double y, double radius, double startAngle)
        {
            for (int step = 0; step <= CornerSteps; step++)
            {
                double angle = startAngle + 0.5 * Math.PI * step / CornerSteps;
                loop.Add(new GlyphVector(x + Math.Cos(angle) * radius, y + Math.Sin(angle) * radius));
            }
        }

        private static double Perimeter(List<GlyphVector> loop)
        {
            double perimeter = 0.0;
            for (int i = 0; i < loop.Count; i++)
            {
                perimeter += GlyphVector.Distance(loop[i], loop[(i + 1) % loop.Count]);
            }

            return perimeter;
        }

        private static List<GlyphVector> Resample(List<GlyphVector> loop, double spacing)
        {
            int count = loop.Count;
            double perimeter = Perimeter(loop);
            int samples = Math.Max(12, (int)Math.Ceiling(perimeter / Math.Max(spacing, 1e-4)));
            double step = perimeter / samples;
            var result = new List<GlyphVector>(samples);
            int edge = 0;
            double edgeStart = 0.0;
            double edgeLength = GlyphVector.Distance(loop[0], loop[1 % count]);
            for (int s = 0; s < samples; s++)
            {
                double target = s * step;
                while (edgeStart + edgeLength < target && edge < count - 1)
                {
                    edgeStart += edgeLength;
                    edge++;
                    edgeLength = GlyphVector.Distance(loop[edge], loop[(edge + 1) % count]);
                }

                double t = edgeLength > 0.0 ? Math.Min(1.0, (target - edgeStart) / edgeLength) : 0.0;
                result.Add(GlyphVector.Lerp(loop[edge], loop[(edge + 1) % count], t));
            }

            return result;
        }

        // Pushes the rim out by a wave with a whole number of periods around it, so it closes.
        private static List<GlyphVector> AddLobes(List<GlyphVector> loop, WaxGlyphPedestalSettings settings, System.Random random)
        {
            int count = loop.Count;
            int lobes = Math.Max(3, (int)Math.Round(Perimeter(loop) * settings.lobesPerUnit));
            double phase1 = random.NextDouble() * 2.0 * Math.PI;
            double phase2 = random.NextDouble() * 2.0 * Math.PI;
            double phase3 = random.NextDouble() * 2.0 * Math.PI;
            var result = new List<GlyphVector>(count);
            for (int i = 0; i < count; i++)
            {
                double u = 2.0 * Math.PI * i / count;
                double wave = 0.6 * Math.Sin(lobes * u + phase1)
                            + 0.3 * Math.Sin((2 * lobes + 1) * u + phase2)
                            + 0.1 * Math.Sin((4 * lobes + 3) * u + phase3);
                double push = Math.Pow(0.5 + 0.5 * wave, 2.0) * settings.lobeAmplitude;

                // The solid is on the left of a counter-clockwise loop, so the right normal points out.
                GlyphVector tangent = (loop[(i + 1) % count] - loop[(i + count - 1) % count]).Normalized();
                result.Add(loop[i] + tangent.RightNormal * push);
            }

            return result;
        }

        private static List<List<GlyphVector>> OuterLoops(List<List<GlyphVector>> loops)
        {
            var result = new List<List<GlyphVector>>();
            foreach (List<GlyphVector> loop in loops)
            {
                List<GlyphVector> cleaned = GlyphPolygon.Clean(loop, CleanTolerance);
                if (cleaned.Count >= 3 && GlyphPolygon.SignedArea(cleaned) > 0.0)
                {
                    result.Add(cleaned);
                }
            }

            return result;
        }

        // The outline plane (x, y) becomes the ground plane (x, -z), and the extrusion depth the
        // height. That is a rotation, so triangle winding and outward normals are kept.
        private static void LayFlat(WaxGlyphGeometry geometry, float halfThickness)
        {
            Vector3[] positions = geometry.Positions;
            Vector3[] normals = geometry.Normals;
            for (int i = 0; i < positions.Length; i++)
            {
                Vector3 p = positions[i];
                positions[i] = new Vector3(p.x, p.z + halfThickness, -p.y);
                Vector3 n = normals[i];
                normals[i] = new Vector3(n.x, n.z, -n.y);
            }
        }

        private static double Lap(Stopwatch stopwatch)
        {
            double milliseconds = stopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Restart();
            return milliseconds;
        }
    }
}
