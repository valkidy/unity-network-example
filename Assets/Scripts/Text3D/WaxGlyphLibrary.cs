using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>
    /// Builds each glyph once and shares its mesh and material between every object that
    /// shows it.
    /// </summary>
    /// <remarks>
    /// Glyph blocks come and go with gameplay, many at a time, and would otherwise rebuild
    /// the same few characters for each one. Geometry is built on the thread pool;
    /// <see cref="TryResolve"/> turns a finished build into Unity objects on the main
    /// thread, so the mesh, texture and material of a glyph are created by the first
    /// object that is waiting for it, once.
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
        public static Entry Request(string fontPath, int codepoint, WaxGlyphSettings settings, Material template)
        {
            if (template == null)
            {
                throw new ArgumentNullException(nameof(template));
            }

            string resolvedFontPath = Path.GetFullPath(
                Path.IsPathRooted(fontPath) ? fontPath : Path.Combine(Application.streamingAssetsPath, fontPath));
            var key = new Key(resolvedFontPath, codepoint, settings, template.GetInstanceID());
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
                Task.Run(() => WaxGlyphBuilder.Build(WaxGlyphFontCache.Load(resolvedFontPath), codepoint, settings)));
            Entries.Add(key, entry);
            return entry;
        }

        /// <summary>
        /// Creates the entry's mesh, map and material once its build has finished. Main thread only.
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

        public sealed class Entry
        {
            private readonly Task<WaxGlyphGeometry> build;
            private readonly Material template;
            private Texture2D distanceMap;

            internal Entry(int codepoint, Material template, Task<WaxGlyphGeometry> build)
            {
                Codepoint = codepoint;
                this.template = template;
                this.build = build;
            }

            public int Codepoint { get; }

            /// <summary>True once the build has finished and <see cref="TryResolve"/> has handled it.</summary>
            public bool IsDone { get; private set; }

            /// <summary>True when the build threw; the exception has been logged.</summary>
            public bool Failed { get; private set; }

            /// <summary>True for a character with no outline, such as a space.</summary>
            public bool IsEmpty { get; private set; }

            /// <summary>True when the font has no glyph for the character.</summary>
            public bool IsMissing { get; private set; }

            public Mesh Mesh { get; private set; }

            public Material Material { get; private set; }

            /// <summary>Object-space XY bounds of the mesh (xMin, yMin, xMax, yMax).</summary>
            public Vector4 LetterBounds { get; private set; }

            public float HalfDepth { get; private set; }

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

                WaxGlyphGeometry geometry = build.Result;
                IsMissing = geometry.GlyphIndex == 0;
                IsEmpty = geometry.IsEmpty;
                if (IsEmpty)
                {
                    return true;
                }

                LetterBounds = geometry.LetterBounds;
                HalfDepth = geometry.HalfDepth;
                Mesh = WaxGlyphAssets.CreateMesh(geometry);
                distanceMap = WaxGlyphAssets.CreateDistanceMap(geometry);
                Material = WaxGlyphAssets.CreateMaterial(template, geometry, distanceMap);
                return true;
            }

            internal void Release()
            {
                DestroyAsset(Mesh);
                DestroyAsset(distanceMap);
                DestroyAsset(Material);
                Mesh = null;
                distanceMap = null;
                Material = null;
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
            private readonly int templateId;

            public Key(string fontPath, int codepoint, WaxGlyphSettings settings, int templateId)
            {
                this.fontPath = fontPath;
                this.codepoint = codepoint;
                this.settings = settings;
                this.templateId = templateId;
            }

            public bool Equals(Key other) =>
                codepoint == other.codepoint &&
                templateId == other.templateId &&
                fontPath == other.fontPath &&
                EqualityComparer<WaxGlyphSettings>.Default.Equals(settings, other.settings);

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(fontPath, codepoint, templateId);
        }
    }
}
