using System;
using System.Collections.Generic;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>
    /// Converts TrueType contours, made of on-curve points and quadratic control points,
    /// into closed polylines.
    /// </summary>
    public static class GlyphOutlineFlattener
    {
        private const int MaxSegmentsPerCurve = 64;

        /// <param name="contour">Contour in font units.</param>
        /// <param name="scale">Object units per font unit.</param>
        /// <param name="tolerance">Largest distance, in object units, a chord may stray from its curve.</param>
        public static List<GlyphVector> Flatten(IReadOnlyList<GlyphOutlinePoint> contour, double scale, double tolerance)
        {
            var result = new List<GlyphVector>();
            int count = contour.Count;
            if (count < 2)
            {
                return result;
            }

            // Two control points in a row imply an on-curve point halfway between them.
            var points = new List<GlyphVector>(count * 2);
            var onCurve = new List<bool>(count * 2);
            for (int i = 0; i < count; i++)
            {
                GlyphOutlinePoint current = contour[i];
                GlyphOutlinePoint next = contour[(i + 1) % count];
                var position = new GlyphVector(current.X * scale, current.Y * scale);
                points.Add(position);
                onCurve.Add(current.OnCurve);
                if (!current.OnCurve && !next.OnCurve)
                {
                    points.Add(GlyphVector.Lerp(position, new GlyphVector(next.X * scale, next.Y * scale), 0.5));
                    onCurve.Add(true);
                }
            }

            int start = onCurve.IndexOf(true);
            int total = points.Count;
            double safeTolerance = Math.Max(tolerance, 1e-9);
            for (int step = 0; step < total;)
            {
                GlyphVector from = points[(start + step) % total];
                result.Add(from);
                if (onCurve[(start + step + 1) % total])
                {
                    step += 1;
                    continue;
                }

                GlyphVector control = points[(start + step + 1) % total];
                GlyphVector to = points[(start + step + 2) % total];

                // A chord spanning 1/n of a quadratic strays at most |p0 - 2p1 + p2| / (4n^2).
                double bend = (from - 2.0 * control + to).Length;
                int segments = (int)Math.Ceiling(Math.Sqrt(bend / (4.0 * safeTolerance)));
                segments = Math.Max(1, Math.Min(MaxSegmentsPerCurve, segments));
                for (int s = 1; s < segments; s++)
                {
                    double t = (double)s / segments;
                    double u = 1.0 - t;
                    result.Add(u * u * from + 2.0 * u * t * control + t * t * to);
                }

                step += 2;
            }

            return result;
        }
    }
}
