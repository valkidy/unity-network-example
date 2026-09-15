using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>
    /// Lays out one line of extruded WaxCandy glyphs, one child object per character.
    /// </summary>
    /// <remarks>
    /// Every character gets its own mesh, edge distance map and material instance,
    /// because WaxCandy V7 places its body layers relative to a single letter's bounds.
    /// Repeated characters share all three.
    ///
    /// Outlines, meshes and distance maps are built on a background thread. The main
    /// thread only creates the Unity objects, and yields a frame whenever that passes
    /// <see cref="mainThreadBudgetMilliseconds"/>, so a long string spreads its uploads
    /// over several frames instead of stalling one. Builds run in Play Mode only.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class WaxGlyphText : MonoBehaviour
    {
        public const string DefaultMaterialResourcePath = "Text3D/WaxCandy/Materials/WaxCandyGlyph";

        [SerializeField]
        private string text = "WaxCandy 2026";

        [Tooltip("TrueType font file. A relative path is resolved under StreamingAssets.")]
        [SerializeField]
        private string fontPath = "Fonts/ComicRelief-Bold.ttf";

        [Tooltip("WaxCandy V7 material instanced per character. Empty loads " + DefaultMaterialResourcePath + " from Resources.")]
        [SerializeField]
        private Material templateMaterial;

        [SerializeField]
        private WaxGlyphSettings settings = WaxGlyphSettings.Default;

        [Tooltip("Extra space between characters, in glyph object units.")]
        [SerializeField]
        private float letterSpacing = 0.08f;

        [Tooltip("Main-thread time spent creating meshes, textures and materials before waiting a frame.")]
        [SerializeField]
        private float mainThreadBudgetMilliseconds = 4f;

        [SerializeField]
        private bool buildOnStart = true;

        private readonly List<GameObject> glyphObjects = new List<GameObject>();
        private readonly List<Object> ownedAssets = new List<Object>();
        private CancellationTokenSource buildCancellation;
        private int buildGeneration;

        public string Text
        {
            get => text;
            set => text = value ?? string.Empty;
        }

        public string FontPath
        {
            get => fontPath;
            set => fontPath = value;
        }

        public Material TemplateMaterial
        {
            get => templateMaterial;
            set => templateMaterial = value;
        }

        public WaxGlyphSettings Settings
        {
            get => settings;
            set => settings = value;
        }

        public bool IsBuilding { get; private set; }

        /// <summary>Report of the last build that finished; null until one does.</summary>
        public WaxGlyphBuildReport LastReport { get; private set; }

        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("WaxGlyphText builds in Play Mode only.", this);
                return;
            }

            buildCancellation?.Cancel();
            buildCancellation = new CancellationTokenSource();
            _ = BuildAsync(++buildGeneration, buildCancellation.Token);
        }

        private void Start()
        {
            if (buildOnStart)
            {
                Rebuild();
            }
        }

        private void OnDestroy()
        {
            buildCancellation?.Cancel();
            ReleaseGlyphs();
        }

        private async Awaitable BuildAsync(int generation, CancellationToken token)
        {
            IsBuilding = true;
            string requestedText = text ?? string.Empty;
            string resolvedFontPath = Path.IsPathRooted(fontPath) ? fontPath : Path.Combine(Application.streamingAssetsPath, fontPath);
            WaxGlyphSettings buildSettings = settings;
            float spacing = letterSpacing;
            double sliceBudget = mainThreadBudgetMilliseconds;
            Material template = templateMaterial != null ? templateMaterial : Resources.Load<Material>(DefaultMaterialResourcePath);
            var report = new WaxGlyphBuildReport(requestedText) { MainThreadId = Thread.CurrentThread.ManagedThreadId };
            try
            {
                if (template == null)
                {
                    throw new InvalidOperationException(
                        $"WaxGlyphText needs a WaxCandy material: assign one or keep {DefaultMaterialResourcePath} in Resources.");
                }

                await Awaitable.BackgroundThreadAsync();
                token.ThrowIfCancellationRequested();
                var background = Stopwatch.StartNew();
                report.BackgroundThreadId = Thread.CurrentThread.ManagedThreadId;
                TrueTypeFont font = WaxGlyphFontCache.Load(resolvedFontPath);
                report.FontLoadMilliseconds = background.Elapsed.TotalMilliseconds;

                List<int> codepoints = ReadCodepoints(requestedText);
                var geometries = new Dictionary<int, WaxGlyphGeometry>();
                foreach (int codepoint in codepoints)
                {
                    token.ThrowIfCancellationRequested();
                    if (!geometries.ContainsKey(codepoint))
                    {
                        geometries.Add(codepoint, WaxGlyphBuilder.Build(font, codepoint, buildSettings));
                    }
                }

                report.BackgroundMilliseconds = background.Elapsed.TotalMilliseconds;

                await Awaitable.MainThreadAsync();
                token.ThrowIfCancellationRequested();
                ReleaseGlyphs();

                var slice = Stopwatch.StartNew();
                var sharedAssets = new Dictionary<int, (Mesh mesh, Material material)>();
                float pen = 0f;
                foreach (int codepoint in codepoints)
                {
                    WaxGlyphGeometry geometry = geometries[codepoint];
                    if (geometry.GlyphIndex == 0)
                    {
                        report.MissingCharacters++;
                    }

                    if (!geometry.IsEmpty)
                    {
                        if (!sharedAssets.TryGetValue(codepoint, out (Mesh mesh, Material material) assets))
                        {
                            Mesh mesh = WaxGlyphAssets.CreateMesh(geometry);
                            Texture2D map = WaxGlyphAssets.CreateDistanceMap(geometry);
                            Material material = WaxGlyphAssets.CreateMaterial(template, geometry, map);
                            ownedAssets.Add(mesh);
                            ownedAssets.Add(map);
                            ownedAssets.Add(material);
                            assets = (mesh, material);
                            sharedAssets.Add(codepoint, assets);
                            report.AddGlyph(geometry);
                        }

                        glyphObjects.Add(CreateGlyphObject(codepoint, pen, assets.mesh, assets.material));
                    }

                    pen += geometry.Advance + spacing;
                    if (slice.Elapsed.TotalMilliseconds >= sliceBudget)
                    {
                        report.RecordMainThreadSlice(slice.Elapsed.TotalMilliseconds);
                        await Awaitable.NextFrameAsync(token);
                        slice.Restart();
                    }
                }

                report.RecordMainThreadSlice(slice.Elapsed.TotalMilliseconds);
                LastReport = report;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                if (generation == buildGeneration)
                {
                    IsBuilding = false;
                }
            }
        }

        private GameObject CreateGlyphObject(int codepoint, float pen, Mesh mesh, Material material)
        {
            var glyphObject = new GameObject($"Glyph {WaxGlyphAssets.Describe(codepoint)}");
            glyphObject.transform.SetParent(transform, false);
            glyphObject.transform.localPosition = new Vector3(pen, 0f, 0f);
            glyphObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            glyphObject.AddComponent<MeshRenderer>().sharedMaterial = material;
            return glyphObject;
        }

        private void ReleaseGlyphs()
        {
            foreach (GameObject glyphObject in glyphObjects)
            {
                if (glyphObject != null)
                {
                    Destroy(glyphObject);
                }
            }

            foreach (Object asset in ownedAssets)
            {
                if (asset != null)
                {
                    Destroy(asset);
                }
            }

            glyphObjects.Clear();
            ownedAssets.Clear();
        }

        // One line only: control characters, line breaks included, are skipped.
        private static List<int> ReadCodepoints(string value)
        {
            var codepoints = new List<int>(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                int codepoint = value[i];
                if (char.IsHighSurrogate(value, i) && i + 1 < value.Length && char.IsLowSurrogate(value, i + 1))
                {
                    codepoint = char.ConvertToUtf32(value[i], value[i + 1]);
                    i++;
                }

                if (codepoint >= 0x20)
                {
                    codepoints.Add(codepoint);
                }
            }

            return codepoints;
        }
    }
}
