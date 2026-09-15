using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>
    /// Builds each glyph, and the pedestal it stands in, once, and shares their meshes and
    /// materials between every object that shows them.
    /// </summary>
    /// <remarks>
    /// Glyph blocks come and go with gameplay, many at a time, and would otherwise rebuild
    /// the same few characters for each one. Geometry is built on the thread pool;
    /// <see cref="TryResolve"/> turns a finished build into Unity objects on the main
    /// thread, so the meshes, textures and materials of a glyph are created by the first
    /// object that is waiting for it, once.
    ///
    /// A pedestal is seeded by its character, so every block showing a character shares
    /// one pedestal as well.
    ///
    /// Everything is released when play stops.
    /// </remarks>
    public static class WaxGlyphLibrary
    {
        private static readonly Dictionary<Key, Entry> Entries = new Dictionary<Key, Entry>();
        private static bool releaseOnQuitRegistered;

        public static int Count => Entries.Count;

        /// <summary>Starts building the glyph unless it was already requested. Main thread only.</summary>
        /// <param name="fontPath">TrueType font file; a relative path is resolved under StreamingAssets.</param>
        /// <param name="pedestal">Also builds the pedestal the glyph stands in, when set.</param>
        public static Entry Request(
            string fontPath,
            int codepoint,
            WaxGlyphSettings settings,
            Material template,
            WaxGlyphPedestalSettings? pedestal = null)
        {
            if (template == null)
            {
                throw new ArgumentNullException(nameof(template));
            }

            string resolvedFontPath = Path.GetFullPath(
                Path.IsPathRooted(fontPath) ? fontPath : Path.Combine(Application.streamingAssetsPath, fontPath));
            var key = new Key(resolvedFontPath, codepoint, settings, pedestal, template.GetInstanceID());
            if (Entries.TryGetValue(key, out Entry entry))
            {
                return entry;
            }

            if (!releaseOnQuitRegistered)
            {
                // Also raised when Play Mode stops in the Editor, where runtime-created
                // assets would otherwise outlive the session.
                Application.quitting += Clear;
                releaseOnQuitRegistered = true;
            }

            entry = new Entry(
                codepoint,
                template,
                pedestal,
                Task.Run(() => Build(resolvedFontPath, codepoint, settings, pedestal)));
            Entries.Add(key, entry);
            return entry;
        }

        /// <summary>
        /// Creates the entry's meshes, maps and materials once its build has finished. Main thread only.
        /// </summary>
        /// <returns>True once the entry is done, whether or not it produced a mesh.</returns>
        public static bool TryResolve(Entry entry) => entry.Resolve();

        /// <summary>Destroys every glyph asset and forgets every request.</summary>
        public static void Clear()
        {
            foreach (Entry entry in Entries.Values)
            {
                entry.Release();
            }

            Entries.Clear();
        }

        // With domain reload disabled, entries from the last session survive into the next.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnEnterPlayMode() => Clear();

        private static BuildResult Build(string fontPath, int codepoint, WaxGlyphSettings settings, WaxGlyphPedestalSettings? pedestal)
        {
            var result = new BuildResult { Glyph = WaxGlyphBuilder.Build(WaxGlyphFontCache.Load(fontPath), codepoint, settings) };
            if (pedestal.HasValue && !result.Glyph.IsEmpty)
            {
                // A pedestal that fails leaves the glyph standing without one.
                try
                {
                    result.Pedestal = WaxGlyphPedestal.Build(result.Glyph, pedestal.Value, codepoint, out List<Vector2> contactSpans);
                    result.ContactSpans = contactSpans;
                }
                catch (Exception exception)
                {
                    result.PedestalError = exception;
                }
            }

            return result;
        }

        internal sealed class BuildResult
        {
            public WaxGlyphGeometry Glyph;
            public WaxGlyphGeometry Pedestal;
            public List<Vector2> ContactSpans;
            public Exception PedestalError;
        }

        public sealed class Entry
        {
            private readonly Task<BuildResult> build;
            private readonly Material template;
            private readonly WaxGlyphPedestalSettings? pedestalSettings;
            private Texture2D distanceMap;
            private Texture2D pedestalDistanceMap;

            internal Entry(int codepoint, Material template, WaxGlyphPedestalSettings? pedestalSettings, Task<BuildResult> build)
            {
                Codepoint = codepoint;
                this.template = template;
                this.pedestalSettings = pedestalSettings;
                this.build = build;
            }

            public int Codepoint { get; }

            /// <summary>True once the build has finished and <see cref="TryResolve"/> has handled it.</summary>
            public bool IsDone { get; private set; }

            /// <summary>True when the glyph build threw; the exception has been logged.</summary>
            public bool Failed { get; private set; }

            /// <summary>True for a character with no outline, such as a space.</summary>
            public bool IsEmpty { get; private set; }

            /// <summary>True when the font has no glyph for the character.</summary>
            public bool IsMissing { get; private set; }

            public Mesh Mesh { get; private set; }

            public Material Material { get; private set; }

            /// <summary>Object-space XY bounds of the glyph mesh (xMin, yMin, xMax, yMax).</summary>
            public Vector4 LetterBounds { get; private set; }

            public float HalfDepth { get; private set; }

            /// <summary>The pedestal mesh; null when none was requested or it failed.</summary>
            public Mesh PedestalMesh { get; private set; }

            public Material PedestalMaterial { get; private set; }

            /// <summary>Object-space bounds of the pedestal mesh, bottom on y = 0.</summary>
            public Bounds PedestalBounds { get; private set; }

            internal bool Resolve()
            {
                if (IsDone || !build.IsCompleted)
                {
                    return IsDone;
                }

                IsDone = true;
                if (!build.IsCompletedSuccessfully)
                {
                    Failed = true;
                    Debug.LogException(
                        build.Exception?.GetBaseException() ??
                        new OperationCanceledException($"Glyph {WaxGlyphAssets.Describe(Codepoint)} build was cancelled."));
                    return true;
                }

                if (template == null)
                {
                    Failed = true;
                    Debug.LogError($"The material for glyph {WaxGlyphAssets.Describe(Codepoint)} was destroyed before it was built.");
                    return true;
                }

                BuildResult result = build.Result;
                WaxGlyphGeometry glyph = result.Glyph;
                IsMissing = glyph.GlyphIndex == 0;
                IsEmpty = glyph.IsEmpty;
                if (IsEmpty)
                {
                    return true;
                }

                LetterBounds = glyph.LetterBounds;
                HalfDepth = glyph.HalfDepth;
                Mesh = WaxGlyphAssets.CreateMesh(glyph);
                distanceMap = WaxGlyphAssets.CreateDistanceMap(glyph);
                Material = WaxGlyphAssets.CreateMaterial(template, glyph, distanceMap);

                if (result.PedestalError != null)
                {
                    Debug.LogException(result.PedestalError);
                }
                else if (result.Pedestal != null && !result.Pedestal.IsEmpty && pedestalSettings.HasValue)
                {
                    PedestalBounds = BoundsOf(result.Pedestal.Positions);
                    PedestalMesh = WaxGlyphAssets.CreateMesh(result.Pedestal);
                    pedestalDistanceMap = WaxGlyphAssets.CreateDistanceMap(result.Pedestal);
                    PedestalMaterial = WaxGlyphAssets.CreatePedestalMaterial(
                        template,
                        glyph,
                        result.Pedestal,
                        pedestalDistanceMap,
                        result.ContactSpans,
                        pedestalSettings.Value.colorReach,
                        pedestalSettings.Value.colorCurve);
                }

                return true;
            }

            internal void Release()
            {
                DestroyAsset(Mesh);
                DestroyAsset(distanceMap);
                DestroyAsset(Material);
                DestroyAsset(PedestalMesh);
                DestroyAsset(pedestalDistanceMap);
                DestroyAsset(PedestalMaterial);
                Mesh = null;
                distanceMap = null;
                Material = null;
                PedestalMesh = null;
                pedestalDistanceMap = null;
                PedestalMaterial = null;
            }

            private static Bounds BoundsOf(Vector3[] positions)
            {
                var bounds = new Bounds(positions[0], Vector3.zero);
                foreach (Vector3 position in positions)
                {
                    bounds.Encapsulate(position);
                }

                return bounds;
            }

            private static void DestroyAsset(Object asset)
            {
                if (asset == null)
                {
                    return;
                }

                if (Application.isPlaying)
                {
                    Object.Destroy(asset);
                }
                else
                {
                    Object.DestroyImmediate(asset);
                }
            }
        }

        private readonly struct Key : IEquatable<Key>
        {
            private readonly string fontPath;
            private readonly int codepoint;
            private readonly WaxGlyphSettings settings;
            private readonly WaxGlyphPedestalSettings? pedestal;
            private readonly int templateId;

            public Key(string fontPath, int codepoint, WaxGlyphSettings settings, WaxGlyphPedestalSettings? pedestal, int templateId)
            {
                this.fontPath = fontPath;
                this.codepoint = codepoint;
                this.settings = settings;
                this.pedestal = pedestal;
                this.templateId = templateId;
            }

            public bool Equals(Key other) =>
                codepoint == other.codepoint &&
                templateId == other.templateId &&
                fontPath == other.fontPath &&
                EqualityComparer<WaxGlyphSettings>.Default.Equals(settings, other.settings) &&
                pedestal.HasValue == other.pedestal.HasValue &&
                (!pedestal.HasValue || EqualityComparer<WaxGlyphPedestalSettings>.Default.Equals(pedestal.Value, other.pedestal.Value));

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(fontPath, codepoint, templateId, pedestal.HasValue);
        }
    }
}
