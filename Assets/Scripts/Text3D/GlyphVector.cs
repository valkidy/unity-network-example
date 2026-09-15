using System;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>
    /// Double-precision 2D point used while a glyph outline is being built.
    /// </summary>
    /// <remarks>
    /// The union step compares intersection points computed from two different edges,
    /// so outlines stay in doubles until the finished mesh is written out as floats.
    /// </remarks>
    public readonly struct GlyphVector : IEquatable<GlyphVector>
    {
        public readonly double X;
        public readonly double Y;

        public GlyphVector(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double LengthSquared => X * X + Y * Y;

        public double Length => Math.Sqrt(X * X + Y * Y);

        /// <summary>This direction turned a quarter counter-clockwise.</summary>
        public GlyphVector LeftNormal => new GlyphVector(-Y, X);

        /// <summary>This direction turned a quarter clockwise.</summary>
        public GlyphVector RightNormal => new GlyphVector(Y, -X);

        public GlyphVector Normalized()
        {
            double length = Length;
            return length > 0.0 ? new GlyphVector(X / length, Y / length) : default;
        }

        public static GlyphVector operator +(GlyphVector a, GlyphVector b) => new GlyphVector(a.X + b.X, a.Y + b.Y);

        public static GlyphVector operator -(GlyphVector a, GlyphVector b) => new GlyphVector(a.X - b.X, a.Y - b.Y);

        public static GlyphVector operator -(GlyphVector a) => new GlyphVector(-a.X, -a.Y);

        public static GlyphVector operator *(GlyphVector a, double scale) => new GlyphVector(a.X * scale, a.Y * scale);

        public static GlyphVector operator *(double scale, GlyphVector a) => new GlyphVector(a.X * scale, a.Y * scale);

        public static GlyphVector operator /(GlyphVector a, double divisor) => new GlyphVector(a.X / divisor, a.Y / divisor);

        public static double Dot(GlyphVector a, GlyphVector b) => a.X * b.X + a.Y * b.Y;

        /// <summary>Z of the 3D cross product: positive when <paramref name="b"/> turns counter-clockwise from <paramref name="a"/>.</summary>
        public static double Cross(GlyphVector a, GlyphVector b) => a.X * b.Y - a.Y * b.X;

        public static double Distance(GlyphVector a, GlyphVector b) => (a - b).Length;

        public static GlyphVector Lerp(GlyphVector a, GlyphVector b, double t) =>
            new GlyphVector(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

        public bool Equals(GlyphVector other) => X == other.X && Y == other.Y;

        public override bool Equals(object obj) => obj is GlyphVector other && Equals(other);

        public override int GetHashCode() => (X.GetHashCode() * 397) ^ Y.GetHashCode();

        public override string ToString() => $"({X:0.#####}, {Y:0.#####})";
    }
}
