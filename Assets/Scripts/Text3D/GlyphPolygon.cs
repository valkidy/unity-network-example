using System;
using System.Collections.Generic;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>
    /// Helpers for closed polylines. A loop's last point connects back to its first.
    /// </summary>
    public static class GlyphPolygon
    {
        /// <summary>Signed area; positive for counter-clockwise loops.</summary>
        public static double SignedArea(IReadOnlyList<GlyphVector> loop)
        {
            double doubled = 0.0;
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
            {
                doubled += GlyphVector.Cross(loop[j], loop[i]);
            }

            return doubled * 0.5;
        }

        public static bool ContainsEvenOdd(IReadOnlyList<GlyphVector> loop, GlyphVector point)
        {
            bool inside = false;
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
            {
                GlyphVector a = loop[j];
                GlyphVector b = loop[i];
                if ((a.Y > point.Y) != (b.Y > point.Y))
                {
                    double x = a.X + (point.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                    if (point.X < x)
                    {
                        inside = !inside;
                    }
                }
            }

            return inside;
        }

        /// <summary>
        /// Winding number of <paramref name="point"/> around all loops together. TrueType
        /// fills every point whose winding number is not zero.
        /// </summary>
        public static int WindingNumber(IReadOnlyList<IReadOnlyList<GlyphVector>> loops, GlyphVector point)
        {
            int winding = 0;
            foreach (IReadOnlyList<GlyphVector> loop in loops)
            {
                for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
                {
                    winding += Crossing(loop[j], loop[i], point);
                }
            }

            return winding;
        }

        /// <summary>
        /// This edge's contribution to the winding number of <paramref name="point"/>:
        /// +1 or -1 when it crosses the ray toward +X going up or down, otherwise 0.
        /// </summary>
        public static int Crossing(GlyphVector from, GlyphVector to, GlyphVector point)
        {
            if (from.Y <= point.Y)
            {
                if (to.Y > point.Y && GlyphVector.Cross(to - from, point - from) > 0.0)
                {
                    return 1;
                }
            }
            else if (to.Y <= point.Y && GlyphVector.Cross(to - from, point - from) < 0.0)
            {
                return -1;
            }

            return 0;
        }

        public static bool TryGetBounds(
            IEnumerable<IReadOnlyList<GlyphVector>> loops,
            out GlyphVector min,
            out GlyphVector max)
        {
            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;
            foreach (IReadOnlyList<GlyphVector> loop in loops)
            {
                foreach (GlyphVector point in loop)
                {
                    minX = Math.Min(minX, point.X);
                    minY = Math.Min(minY, point.Y);
                    maxX = Math.Max(maxX, point.X);
                    maxY = Math.Max(maxY, point.Y);
                }
            }

            min = new GlyphVector(minX, minY);
            max = new GlyphVector(maxX, maxY);
            return minX <= maxX;
        }

        /// <summary>
        /// Copies a loop without repeated points, points that lie within
        /// <paramref name="tolerance"/> of the line through their neighbours, and
        /// zero-width spikes. Returns an empty list when fewer than three points remain.
        /// </summary>
        public static List<GlyphVector> Clean(IReadOnlyList<GlyphVector> loop, double tolerance)
        {
            var points = new List<GlyphVector>(loop.Count);
            foreach (GlyphVector point in loop)
            {
                if (points.Count == 0 || GlyphVector.Distance(points[points.Count - 1], point) > tolerance)
                {
                    points.Add(point);
                }
            }

            while (points.Count > 1 && GlyphVector.Distance(points[0], points[points.Count - 1]) <= tolerance)
            {
                points.RemoveAt(points.Count - 1);
            }

            bool changed = true;
            while (changed && points.Count >= 3)
            {
                changed = false;
                for (int i = 0; i < points.Count && points.Count >= 3;)
                {
                    GlyphVector previous = points[(i + points.Count - 1) % points.Count];
                    GlyphVector current = points[i];
                    GlyphVector next = points[(i + 1) % points.Count];
                    GlyphVector chord = next - previous;
                    double chordLength = chord.Length;
                    bool degenerate =
                        GlyphVector.Distance(previous, current) <= tolerance ||
                        GlyphVector.Distance(current, next) <= tolerance ||
                        chordLength <= tolerance ||
                        Math.Abs(GlyphVector.Cross(chord, current - previous)) <= tolerance * chordLength;
                    if (degenerate)
                    {
                        points.RemoveAt(i);
                        changed = true;
                    }
                    else
                    {
                        i++;
                    }
                }
            }

            if (points.Count < 3)
            {
                points.Clear();
            }

            return points;
        }

        /// <summary>True when the segments cross or touch.</summary>
        public static bool SegmentsIntersect(GlyphVector a, GlyphVector b, GlyphVector c, GlyphVector d)
        {
            double d1 = GlyphVector.Cross(d - c, a - c);
            double d2 = GlyphVector.Cross(d - c, b - c);
            double d3 = GlyphVector.Cross(b - a, c - a);
            double d4 = GlyphVector.Cross(b - a, d - a);
            if (((d1 > 0.0 && d2 < 0.0) || (d1 < 0.0 && d2 > 0.0)) &&
                ((d3 > 0.0 && d4 < 0.0) || (d3 < 0.0 && d4 > 0.0)))
            {
                return true;
            }

            return (d1 == 0.0 && WithinBox(c, d, a)) ||
                   (d2 == 0.0 && WithinBox(c, d, b)) ||
                   (d3 == 0.0 && WithinBox(a, b, c)) ||
                   (d4 == 0.0 && WithinBox(a, b, d));
        }

        /// <summary>Inclusive point-in-triangle test for either winding.</summary>
        public static bool TriangleContains(GlyphVector a, GlyphVector b, GlyphVector c, GlyphVector point)
        {
            if (point.X < Math.Min(a.X, Math.Min(b.X, c.X)) || point.X > Math.Max(a.X, Math.Max(b.X, c.X)) ||
                point.Y < Math.Min(a.Y, Math.Min(b.Y, c.Y)) || point.Y > Math.Max(a.Y, Math.Max(b.Y, c.Y)))
            {
                return false;
            }

            double d1 = GlyphVector.Cross(b - a, point - a);
            double d2 = GlyphVector.Cross(c - b, point - b);
            double d3 = GlyphVector.Cross(a - c, point - c);
            bool hasNegative = d1 < 0.0 || d2 < 0.0 || d3 < 0.0;
            bool hasPositive = d1 > 0.0 || d2 > 0.0 || d3 > 0.0;
            return !(hasNegative && hasPositive);
        }

        public static double DistanceToSegmentSquared(GlyphVector point, GlyphVector a, GlyphVector b)
        {
            GlyphVector ab = b - a;
            double lengthSquared = ab.LengthSquared;
            double t = lengthSquared > 0.0
                ? Math.Max(0.0, Math.Min(1.0, GlyphVector.Dot(point - a, ab) / lengthSquared))
                : 0.0;
            return (point - (a + ab * t)).LengthSquared;
        }

        private static bool WithinBox(GlyphVector a, GlyphVector b, GlyphVector point) =>
            point.X >= Math.Min(a.X, b.X) && point.X <= Math.Max(a.X, b.X) &&
            point.Y >= Math.Min(a.Y, b.Y) && point.Y <= Math.Max(a.Y, b.Y);
    }
}
