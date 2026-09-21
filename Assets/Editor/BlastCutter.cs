using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NetworkExample.UnityDemo.Shatter;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace NetworkExample.UnityDemo.EditorTools
{
    /// <summary>
    /// Cuts a closed part with Blast, through the blast_cut tool
    /// Tools/BlastSpike builds.
    /// </summary>
    /// <remarks>
    /// A separate process rather than a native plugin, because the cut is a bake:
    /// it runs on the machine that bakes, when it bakes, and nothing the player
    /// ships ever loads Blast. The price is that the tool has to be built before
    /// it can be used -- see Tools/BlastSpike/README.md -- and a machine without
    /// it bakes the pieces the project cuts itself.
    ///
    /// The cut is Blast's noisy slicing: planes through the part, each bent by
    /// noise into a ragged break, with the faces it makes on a material of their
    /// own. That is the whole reason to hand a part to Blast; its voronoi cut has
    /// no noise and is no better than the project's own.
    ///
    /// It only ever sees closed parts, and it still checks what comes back,
    /// because closed is not the same as solid. The tower's roof is closed, and
    /// Blast's slicing answers it -- with no noise at all -- with a third more
    /// surface than the roof has, in fins that stand out of it; its voronoi cut
    /// of the same roof is exact. So a cut is only kept if the model's own
    /// surface comes back the size it went in: a cut divides that surface and
    /// adds faces of its own, and the added faces are kept apart. Noisy slices
    /// are tried first, Blast's voronoi cells second, and a part that fails both
    /// goes back to being cut by the project's own clipper.
    /// </remarks>
    public sealed class BlastCutter
    {
        /// <summary>Where Tools/BlastSpike's build leaves the tool, from the project root.</summary>
        public const string DefaultExecutable = "Tools/BlastSpike/build/blast_cut";

        private const int TimeoutMilliseconds = 120000;

        private readonly string executable;
        private readonly string workDirectory;

        private BlastCutter(string executable, string workDirectory)
        {
            this.executable = executable;
            this.workDirectory = workDirectory;
        }

        /// <summary>Roughly how far a piece should reach, which decides the slice counts.</summary>
        public float TargetPieceExtent { get; set; } = MeshIslandFracturer.DefaultTargetPieceExtent;

        /// <summary>Most pieces one part is cut into.</summary>
        public int MaxPieces { get; set; } = MeshIslandFracturer.MaxPiecesPerPart;

        /// <summary>How far a break bends away from its plane, in metres.</summary>
        public float NoiseAmplitude { get; set; } = 0.08f;

        /// <summary>How tightly the bends repeat.</summary>
        public float NoiseFrequency { get; set; } = 2f;

        /// <summary>
        /// The grid a break is sampled on, in metres. What a cut face's triangle
        /// count scales with: halving it roughly quadruples them.
        /// </summary>
        public float SamplingInterval { get; set; } = 0.2f;

        /// <summary>
        /// How far the model's surface may come back from its size before a cut
        /// is refused, as a fraction of it.
        /// </summary>
        public float SurfaceTolerance { get; set; } = 0.005f;

        /// <summary>Parts cut into noisy slices.</summary>
        public int CutNoisy { get; private set; }

        /// <summary>Parts whose noisy slices were refused and that were cut into Blast's voronoi cells instead.</summary>
        public int CutFlat { get; private set; }

        /// <summary>Parts none of Blast's answers could be used for.</summary>
        public int Refused { get; private set; }

        /// <summary>
        /// The cutter, if blast_cut has been built; null when it has not.
        /// </summary>
        public static BlastCutter Find()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string path = Path.Combine(projectRoot, DefaultExecutable);
            if (!File.Exists(path))
            {
                return null;
            }

            return new BlastCutter(path, Path.Combine(projectRoot, "Temp", "BlastCut"));
        }

        /// <summary>
        /// The part in noisy slices, or nothing when Blast failed or answered
        /// with something that cannot be the part -- which the caller takes as
        /// a reason to cut it some other way.
        /// </summary>
        public List<MeshIsland> Cut(MeshIsland part, int seed)
        {
            var none = new List<MeshIsland>();
            Vector3Int slices = Slices(part.LocalBounds.size);
            if (slices == Vector3Int.zero)
            {
                return none;
            }

            Directory.CreateDirectory(workDirectory);
            string input = Path.Combine(workDirectory, "part_" + seed + ".obj");
            File.WriteAllText(input, BlastObj.Write(part));
            float surface = SurfaceArea(part);

            List<MeshIsland> noisy = RunCut(
                part,
                input,
                seed,
                "slice " + slices.x + " " + slices.y + " " + slices.z + " " + F(NoiseAmplitude) +
                    " " + F(NoiseFrequency) + " " + seed + " " + F(SamplingInterval),
                surface,
                out string noisyReason);
            if (noisy != null)
            {
                ++CutNoisy;
                return noisy;
            }

            int cells = (slices.x + 1) * (slices.y + 1) * (slices.z + 1);
            List<MeshIsland> flat = RunCut(
                part,
                input,
                seed,
                "voronoi " + cells + " " + seed,
                surface,
                out string flatReason);
            if (flat != null)
            {
                ++CutFlat;
                Debug.Log(
                    "Blast's noisy slices of the part at " + part.Pivot + " were refused (" +
                    noisyReason + "); cut into Blast's voronoi cells instead.");
                return flat;
            }

            ++Refused;
            Debug.LogWarning(
                "Blast could not cut the part at " + part.Pivot + ": slices " + noisyReason +
                "; voronoi " + flatReason + ". Cutting it with the project's own clipper.");
            return none;
        }

        /// <summary>
        /// One run of blast_cut, and its answer if the answer is the part: null,
        /// with the reason, if it failed or came back as something else.
        /// </summary>
        private List<MeshIsland> RunCut(
            MeshIsland part,
            string input,
            int seed,
            string cut,
            float surface,
            out string reason)
        {
            string output = Path.Combine(workDirectory, "part_" + seed + "_cut.obj");
            File.Delete(output);
            if (!Run(Quote(input) + " " + Quote(output) + " " + cut, out string log))
            {
                reason = "blast_cut failed: " + Tail(log);
                return null;
            }

            List<MeshIsland> pieces = BlastObj.Read(File.ReadAllText(output), part.Pivot);
            if (pieces.Count < 2)
            {
                reason = "it came back in " + pieces.Count + " piece(s)";
                return null;
            }

            float kept = 0f;
            for (int index = 0; index < pieces.Count; ++index)
            {
                kept += SurfaceArea(pieces[index]);
            }

            float drift = surface > 0f ? Mathf.Abs(kept - surface) / surface : 0f;
            if (drift > SurfaceTolerance)
            {
                reason = "the model's surface came back " + (kept / surface).ToString("P1") + " of its size";
                return null;
            }

            if (!StaysInside(part, pieces))
            {
                reason = "pieces reach outside the part";
                return null;
            }

            reason = null;
            return pieces;
        }

        /// <summary>
        /// Area of the model's own surface on a piece -- its surface triangles,
        /// not the faces a cut made.
        /// </summary>
        public static float SurfaceArea(MeshIsland piece)
        {
            float area = 0f;
            int[] triangles = piece.Triangles;
            Vector3[] positions = piece.Positions;
            for (int start = 0; start + 2 < triangles.Length; start += 3)
            {
                Vector3 a = positions[triangles[start]];
                area += Vector3.Cross(
                    positions[triangles[start + 1]] - a,
                    positions[triangles[start + 2]] - a).magnitude * 0.5f;
            }

            return area;
        }

        private static string Tail(string log)
        {
            return log.Length <= 400 ? log : "..." + log.Substring(log.Length - 400);
        }

        /// <summary>
        /// Cuts along each axis for a piece of about the target size, fewest cuts
        /// first taken off the most-cut axis until the total is under the
        /// ceiling. Zero when the part would come out in one piece.
        /// </summary>
        public Vector3Int Slices(Vector3 size)
        {
            float target = Mathf.Max(0.01f, TargetPieceExtent);
            int x = Mathf.Max(1, Mathf.RoundToInt(size.x / target));
            int y = Mathf.Max(1, Mathf.RoundToInt(size.y / target));
            int z = Mathf.Max(1, Mathf.RoundToInt(size.z / target));
            while (x * y * z > Mathf.Max(2, MaxPieces))
            {
                if (x >= y && x >= z)
                {
                    --x;
                }
                else if (y >= z)
                {
                    --y;
                }
                else
                {
                    --z;
                }
            }

            return x * y * z < 2 ? Vector3Int.zero : new Vector3Int(x - 1, y - 1, z - 1);
        }

        private bool Run(string arguments, out string log)
        {
            var start = new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (Process process = Process.Start(start))
            {
                // Both streams read at once: Blast can write enough warnings to
                // fill one pipe while this waits on the other.
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(TimeoutMilliseconds))
                {
                    process.Kill();
                    log = "timed out after " + (TimeoutMilliseconds / 1000) + " s";
                    return false;
                }

                log = stdout.Result + stderr.Result;
                return process.ExitCode == 0;
            }
        }

        /// <summary>
        /// Whether the pieces keep within the part they were cut from, give or
        /// take the noise a break can bend by.
        /// </summary>
        private bool StaysInside(MeshIsland part, List<MeshIsland> pieces)
        {
            // Bounds.Expand grows the size, so each side moves by half of it: a
            // break may bend out by the noise it was given, and a centimetre more.
            var allowed = new Bounds(part.Pivot, part.LocalBounds.size);
            allowed.Expand(2f * (NoiseAmplitude + 0.01f));
            for (int index = 0; index < pieces.Count; ++index)
            {
                MeshIsland piece = pieces[index];
                if (!allowed.Contains(piece.Pivot + piece.LocalBounds.min) ||
                    !allowed.Contains(piece.Pivot + piece.LocalBounds.max))
                {
                    return false;
                }
            }

            return true;
        }

        private static string Quote(string path)
        {
            return "\"" + path + "\"";
        }

        private static string F(float value)
        {
            return value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
