using System.Collections.Generic;
using NetworkExample.UnityDemo.Text3D;
using UnityEngine;

namespace NetworkExample.UnityDemo.Shatter
{
    /// <summary>
    /// Cuts the parts a model is too big to come apart into on its own.
    /// </summary>
    /// <remarks>
    /// <see cref="MeshIslandSplitter"/> gives the pieces the artist modelled, and
    /// for most of a building that is already debris: a plank, a shingle, a
    /// window. What it cannot give is a piece smaller than a part, and a tower's
    /// body is one part three and a half metres across -- thrown whole it reads
    /// as a building hopping, not as one coming down.
    ///
    /// Those parts are cut into voronoi cells. A cell is what is left of the part
    /// after it has been cut by the plane halfway to every other cell's point, so
    /// the whole fracture is <see cref="MeshPlaneClipper"/> run once per pair:
    /// every piece is convex-cut, the pieces tile the part exactly, and no two
    /// overlap. The points are drawn from the part's own bounds, spread apart far
    /// enough that a cell is rarely a sliver.
    ///
    /// It is a bake, so it is allowed to be slow and it has to be repeatable: the
    /// points come from a generator seeded per part, never from the global one,
    /// so the same model always cuts into the same pieces and a test can say so.
    /// </remarks>
    public static class MeshIslandFracturer
    {
        /// <summary>
        /// Cuts one closed part some other way than voronoi cells. Returns the
        /// pieces, or fewer than two to decline, in which case the part is cut
        /// into cells after all.
        /// </summary>
        public delegate List<MeshIsland> ClosedPartCutter(MeshIsland part, int seed);

        /// <summary>
        /// Parts reaching further than this across are cut down. The tower's
        /// biggest parts are its body and the ground it stands on; everything
        /// else it is modelled in is already debris-sized.
        /// </summary>
        public const float DefaultCutAboveExtent = 3f;

        /// <summary>
        /// Roughly how far a cut piece should reach, which is what decides how
        /// many a part is cut into.
        /// </summary>
        public const float DefaultTargetPieceExtent = 1.2f;

        /// <summary>
        /// A ceiling on the pieces one part is cut into, so a part far larger
        /// than the target does not turn into a cloud of confetti with a renderer
        /// each.
        /// </summary>
        public const int MaxPiecesPerPart = 12;

        /// <summary>
        /// What the bake seeds from. Changing it re-cuts every part differently.
        /// </summary>
        public const int DefaultSeed = 1;

        /// <summary>
        /// The whole model's parts, with the big ones cut down, largest first.
        /// </summary>
        public static List<MeshIsland> Fracture(IReadOnlyList<MeshIsland> parts)
        {
            return Fracture(
                parts,
                DefaultCutAboveExtent,
                DefaultTargetPieceExtent,
                DefaultSeed,
                out _,
                out _);
        }

        /// <param name="partsCut">How many parts were cut into more than one piece.</param>
        /// <param name="unclosedCuts">
        /// Cuts that ran off an edge the model already had, and so were left
        /// open. Counted rather than hidden: a part that comes back full of them
        /// is a part whose pieces will show their insides.
        /// </param>
        public static List<MeshIsland> Fracture(
            IReadOnlyList<MeshIsland> parts,
            float cutAboveExtent,
            float targetPieceExtent,
            int seed,
            out int partsCut,
            out int unclosedCuts)
        {
            return Fracture(
                parts,
                cutAboveExtent,
                targetPieceExtent,
                seed,
                null,
                out partsCut,
                out _,
                out unclosedCuts);
        }

        /// <param name="closedPartCutter">
        /// Takes the parts that are both too big and closed, and cuts them its
        /// own way -- Blast, in the bake. Null cuts everything into voronoi
        /// cells. A part with a rim is never offered: a cutter that works by
        /// boolean operations cannot tell the inside of an open surface from the
        /// outside, and the voronoi clip leaves such a cut open rather than
        /// guessing.
        /// </param>
        /// <param name="partsCutByCutter">How many of the cut parts the cutter took.</param>
        public static List<MeshIsland> Fracture(
            IReadOnlyList<MeshIsland> parts,
            float cutAboveExtent,
            float targetPieceExtent,
            int seed,
            ClosedPartCutter closedPartCutter,
            out int partsCut,
            out int partsCutByCutter,
            out int unclosedCuts)
        {
            partsCut = 0;
            partsCutByCutter = 0;
            unclosedCuts = 0;
            var pieces = new List<MeshIsland>();
            if (parts == null)
            {
                return pieces;
            }

            for (int index = 0; index < parts.Count; ++index)
            {
                MeshIsland part = parts[index];
                Vector3 size = part.LocalBounds.size;
                float extent = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
                if (extent <= cutAboveExtent)
                {
                    pieces.Add(part);
                    continue;
                }

                if (closedPartCutter != null && part.IsClosed())
                {
                    List<MeshIsland> byCutter = closedPartCutter(part, SeedFor(seed, index));
                    if (byCutter != null && byCutter.Count >= 2)
                    {
                        ++partsCut;
                        ++partsCutByCutter;
                        pieces.AddRange(byCutter);
                        continue;
                    }
                }

                int wanted = Mathf.Clamp(
                    Mathf.RoundToInt(extent / Mathf.Max(0.01f, targetPieceExtent)),
                    2,
                    MaxPiecesPerPart);
                List<MeshIsland> cut = Fracture(
                    part,
                    wanted,
                    SeedFor(seed, index),
                    out int partUnclosed);
                unclosedCuts += partUnclosed;
                if (cut.Count < 2)
                {
                    // One cell took the whole part, or the cells took none of it.
                    // Either way the part is better off whole than re-centred for
                    // nothing.
                    pieces.Add(part);
                    continue;
                }

                ++partsCut;
                pieces.AddRange(cut);
            }

            MeshIslandSplitter.SortLargestFirst(pieces);
            return pieces;
        }

        /// <summary>
        /// One part cut into at most <paramref name="wanted"/> voronoi cells.
        /// Cells that caught none of the part are not returned, so the result can
        /// be shorter than asked for.
        /// </summary>
        public static List<MeshIsland> Fracture(
            MeshIsland part,
            int wanted,
            int seed,
            out int unclosedCuts)
        {
            unclosedCuts = 0;
            var pieces = new List<MeshIsland>();
            if (part == null || wanted < 2)
            {
                return pieces;
            }

            var random = new Xorshift(seed);
            List<Vector3> sites = PickSites(part.LocalBounds, wanted, random);
            ShatterGeometry whole = ShatterGeometry.From(part);
            for (int cell = 0; cell < sites.Count; ++cell)
            {
                ShatterGeometry geometry = whole;
                for (int other = 0; other < sites.Count && geometry != null; ++other)
                {
                    if (other == cell)
                    {
                        continue;
                    }

                    Vector3 away = sites[other] - sites[cell];
                    if (away.sqrMagnitude < 1e-8f)
                    {
                        continue;
                    }

                    // Everything closer to this cell's point than to the other's
                    // is behind the plane halfway between them.
                    var plane = new Plane(
                        away.normalized,
                        (sites[cell] + sites[other]) * 0.5f);
                    geometry = MeshPlaneClipper.Clip(geometry, plane, out int unclosed);
                    unclosedCuts += unclosed;
                }

                if (geometry != null && geometry.TriangleCount > 0)
                {
                    pieces.Add(geometry.ToIsland(part.Pivot));
                }
            }

            return pieces;
        }

        /// <summary>
        /// Points to cut around, kept apart so a cell is rarely a sliver.
        /// </summary>
        /// <remarks>
        /// Drawn from the part's bounds rather than from its surface, so a hollow
        /// part is cut by planes through its hollow as well -- which is what
        /// makes a cell a wedge of the whole shell instead of a patch of one
        /// wall. A point that lands too close to one already taken is redrawn a
        /// few times and then accepted anyway: the spacing is worth trying for,
        /// not worth failing the bake over.
        /// </remarks>
        private static List<Vector3> PickSites(Bounds bounds, int count, Xorshift random)
        {
            var sites = new List<Vector3>(count);
            Vector3 size = bounds.size;
            float volume = Mathf.Max(size.x * size.y * size.z, 1e-6f);
            float spacing = Mathf.Pow(volume / count, 1f / 3f) * 0.6f;
            for (int index = 0; index < count; ++index)
            {
                Vector3 candidate = Vector3.zero;
                for (int attempt = 0; attempt < 24; ++attempt)
                {
                    candidate = new Vector3(
                        random.Range(bounds.min.x, bounds.max.x),
                        random.Range(bounds.min.y, bounds.max.y),
                        random.Range(bounds.min.z, bounds.max.z));
                    if (IsClearOf(sites, candidate, spacing))
                    {
                        break;
                    }
                }

                sites.Add(candidate);
            }

            return sites;
        }

        private static bool IsClearOf(List<Vector3> sites, Vector3 candidate, float spacing)
        {
            for (int index = 0; index < sites.Count; ++index)
            {
                if (Vector3.Distance(sites[index], candidate) < spacing)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The seed one part cuts with. Scattered so that neighbouring parts of
        /// the same bake are not cut with generators a step apart, which is the
        /// reason <see cref="GlyphCharacterSet.Mix"/> exists.
        /// </summary>
        private static int SeedFor(int seed, int part)
        {
            ulong mixed = GlyphCharacterSet.Mix(((ulong)(uint)seed << 20) + (ulong)(uint)part);
            return (int)(mixed & 0x7FFFFFFFUL);
        }

        /// <summary>
        /// A generator the bake owns, so cutting a model does not depend on -- or
        /// disturb -- whatever else is drawing from Unity's.
        /// </summary>
        private sealed class Xorshift
        {
            private uint state;

            public Xorshift(int seed)
            {
                state = (uint)seed;
                if (state == 0u)
                {
                    state = 0x9E3779B9u;
                }
            }

            public float Range(float min, float max)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                return min + ((max - min) * ((state >> 8) * (1f / 16777216f)));
            }
        }
    }
}
