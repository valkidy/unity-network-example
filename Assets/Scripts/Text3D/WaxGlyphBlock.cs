using System;
using UnityEngine;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>
    /// One WaxCandy glyph standing on the ground, picked from a character set and fitted
    /// inside the entity's hitbox.
    /// </summary>
    /// <remarks>
    /// The kernel replicates no per-instance value for a prop, so the character is picked
    /// on each client from a seed every client already agrees on -- the entity's net id --
    /// through <see cref="GlyphCharacterSet.Pick"/>. The server never knows which
    /// character a block shows.
    ///
    /// The glyph is built with its bottom center at the origin and scaled so a capital is
    /// <see cref="capHeightInBox"/> of the box height, shrinking any glyph that would
    /// still leave the box. Assets come from <see cref="WaxGlyphLibrary"/>, so blocks that
    /// show the same character share them. Builds run in Play Mode only.
    ///
    /// Glyph outlines run +X to the right as seen looking toward +Z, so an unturned glyph
    /// reads from its -Z side. That is the side a thrower sees, because the kernel yaws a
    /// spawned block so its +Z follows the throw direction.
    ///
    /// By default the glyph stands sunk into a melted-wax pedestal (<see cref="WaxGlyphPedestal"/>)
    /// that wears the glyph's own layers, spreading out from where it stands. The pedestal's
    /// width and height count toward fitting the box; its depth does not, because the pool
    /// spreads out on the ground past the hitbox's thin slab.
    ///
    /// When the material's melt flow is on, it starts when the character is assigned: the time
    /// is written to each part's renderer user value, which the shader reads without breaking
    /// SRP batching. A client that first sees a block partway through its life starts the flow
    /// from the top all the same.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class WaxGlyphBlock : MonoBehaviour
    {
        public const string DefaultCharacterSet = "[a-zA-Z0-9]";

        [Tooltip("Characters to pick from, as one regular-expression character class.")]
        [SerializeField]
        private string characterSet = DefaultCharacterSet;

        [Tooltip("TrueType font file. A relative path is resolved under StreamingAssets.")]
        [SerializeField]
        private string fontPath = "Fonts/ComicRelief-Bold.ttf";

        [Tooltip("WaxCandy V7 material instanced per character. Empty loads " + WaxGlyphText.DefaultMaterialResourcePath + " from Resources.")]
        [SerializeField]
        private Material templateMaterial;

        [Tooltip("Glyph build settings. The pivot is always the bottom center, whatever is set here.")]
        [SerializeField]
        private WaxGlyphSettings settings = WaxGlyphSettings.Default;

        [Tooltip("Width, height and depth the glyph stays inside. Match the entity's collider template: its half extents doubled.")]
        [SerializeField]
        private Vector3 boxSize = new Vector3(1f, 1f, 0.2f);

        [Tooltip("Height of a capital letter as a fraction of the box height, before larger glyphs are shrunk to fit.")]
        [Range(0.1f, 1f)]
        [SerializeField]
        private float capHeightInBox = 0.8f;

        [Tooltip("Clearance kept between the glyph and the sides and top of the box.")]
        [SerializeField]
        private float boxMargin = 0.03f;

        [Tooltip("Stands the glyph in a melted-wax pedestal that wears the glyph's own layers.")]
        [SerializeField]
        private bool buildPedestal = true;

        [SerializeField]
        private WaxGlyphPedestalSettings pedestalSettings = WaxGlyphPedestalSettings.Default;

        [Tooltip("Turns the glyph around so it reads from +Z. Unturned it reads from -Z, where the thrower stands: " +
                 "the kernel yaws a spawned entity so +Z follows the throw direction.")]
        [SerializeField]
        private bool faceBackward;

        [Tooltip("Builds every character in the set in the background the first time a block asks, so later blocks appear at once.")]
        [SerializeField]
        private bool prewarmCharacterSet = true;

        [Tooltip("Seed used when nothing assigns one before Start, so the prefab shows a glyph on its own.")]
        [SerializeField]
        private uint previewSeed;

        private const int MaxPrewarmCharacters = 512;

        private GlyphCharacterSet parsedSet;
        private WaxGlyphLibrary.Entry entry;
        private GameObject glyphObject;
        private GameObject pedestalObject;
        private bool seedAssigned;
        private float flowStartTime;

        /// <summary>The character picked by the last seed; -1 before one is assigned.</summary>
        public int Codepoint { get; private set; } = -1;

        /// <summary>The child that draws the glyph; null until its assets are ready.</summary>
        public GameObject GlyphObject => glyphObject;

        /// <summary>The child that draws the pedestal; null until its assets are ready, or without one.</summary>
        public GameObject PedestalObject => pedestalObject;

        /// <summary><see cref="Time.timeSinceLevelLoad"/> when the current character was assigned; the melt flow starts there.</summary>
        public float FlowStartTime => flowStartTime;

        /// <summary>
        /// Renderer user value the shader's melt flow reads as its start: hundredths of a second
        /// since the level loaded, the clock <c>_Time.y</c> runs on.
        /// </summary>
        public static uint FlowStartUserValue(float timeSinceLevelLoad) =>
            (uint)Mathf.Round(Mathf.Max(0f, timeSinceLevelLoad) * 100f);

        public Vector3 BoxSize => boxSize;

        /// <summary>Picks the character for <paramref name="seed"/> and builds it if it changed.</summary>
        public void AssignSeed(ulong seed)
        {
            seedAssigned = true;
            if (!Application.isPlaying)
            {
                Debug.LogWarning("WaxGlyphBlock builds in Play Mode only.", this);
                return;
            }

            if (!TryGetCharacterSet(out GlyphCharacterSet set))
            {
                return;
            }

            int codepoint = set.Pick(seed);
            if (codepoint == Codepoint && entry != null)
            {
                return;
            }

            Material template = templateMaterial != null
                ? templateMaterial
                : Resources.Load<Material>(WaxGlyphText.DefaultMaterialResourcePath);
            if (template == null)
            {
                Debug.LogError(
                    $"WaxGlyphBlock needs a WaxCandy material: assign one or keep {WaxGlyphText.DefaultMaterialResourcePath} in Resources.",
                    this);
                return;
            }

            WaxGlyphSettings buildSettings = settings;
            buildSettings.pivot = WaxGlyphPivot.BottomCenter;
            WaxGlyphPedestalSettings? pedestal = buildPedestal ? pedestalSettings : (WaxGlyphPedestalSettings?)null;
            if (prewarmCharacterSet && set.Count <= MaxPrewarmCharacters)
            {
                foreach (int character in set.EnumerateCodepoints())
                {
                    WaxGlyphLibrary.Request(fontPath, character, buildSettings, template, pedestal);
                }
            }

            DestroyParts();
            Codepoint = codepoint;
            flowStartTime = Time.timeSinceLevelLoad;
            entry = WaxGlyphLibrary.Request(fontPath, codepoint, buildSettings, template, pedestal);
            enabled = true;
        }

        /// <summary>
        /// XY bounds (xMin, yMin, xMax, yMax) of everything a block shows standing on the
        /// ground, mirrored about x = 0 for <see cref="FitScale"/>: the glyph lifted by
        /// <paramref name="lift"/>, and the pedestal's footprint across x and its height.
        /// </summary>
        /// <param name="glyphBounds">Mesh XY bounds of a glyph built with its bottom center at the origin.</param>
        /// <param name="pedestalBounds">Pedestal mesh bounds, bottom on y = 0; null for a glyph without one.</param>
        public static Vector4 StandingBounds(Vector4 glyphBounds, Bounds? pedestalBounds, float lift)
        {
            float halfWidth = Mathf.Max(Mathf.Abs(glyphBounds.x), Mathf.Abs(glyphBounds.z));
            float top = glyphBounds.w + lift;
            if (pedestalBounds.HasValue)
            {
                Bounds pedestal = pedestalBounds.Value;
                halfWidth = Mathf.Max(halfWidth, Mathf.Max(Mathf.Abs(pedestal.min.x), Mathf.Abs(pedestal.max.x)));
                top = Mathf.Max(top, pedestal.max.y);
            }

            return new Vector4(-halfWidth, 0f, halfWidth, top);
        }

        /// <summary>
        /// Uniform scale that makes a capital <paramref name="capHeightInBox"/> of the box
        /// height, reduced until a glyph with these bounds fits the box.
        /// </summary>
        /// <param name="letterBounds">XY bounds (xMin, yMin, xMax, yMax) standing on the origin and centered on x, as <see cref="StandingBounds"/> gives.</param>
        public static float FitScale(
            Vector4 letterBounds,
            float halfDepth,
            float capHeight,
            Vector3 box,
            float capHeightInBox,
            float margin)
        {
            float scale = capHeight > 0f ? capHeightInBox * box.y / capHeight : float.PositiveInfinity;
            float width = letterBounds.z - letterBounds.x;
            float height = letterBounds.w - letterBounds.y;
            if (width > 0f)
            {
                scale = Mathf.Min(scale, (box.x - 2f * margin) / width);
            }

            if (height > 0f)
            {
                scale = Mathf.Min(scale, (box.y - margin) / height);
            }

            if (halfDepth > 0f)
            {
                scale = Mathf.Min(scale, box.z / (2f * halfDepth));
            }

            return float.IsInfinity(scale) ? 1f : Mathf.Max(0f, scale);
        }

        private void Start()
        {
            if (!seedAssigned)
            {
                AssignSeed(previewSeed);
            }
        }

        private void Update()
        {
            if (entry == null)
            {
                enabled = false;
                return;
            }

            if (!WaxGlyphLibrary.TryResolve(entry))
            {
                return;
            }

            enabled = false;
            if (entry.IsMissing)
            {
                Debug.LogWarning($"Font {fontPath} has no glyph for {WaxGlyphAssets.Describe(Codepoint)}.", this);
            }

            if (entry.Failed || entry.IsEmpty)
            {
                return;
            }

            bool hasPedestal = entry.PedestalMesh != null;
            float lift = hasPedestal ? pedestalSettings.glyphLift : 0f;
            float scale = FitScale(
                StandingBounds(entry.LetterBounds, hasPedestal ? entry.PedestalBounds : (Bounds?)null, lift),
                entry.HalfDepth,
                settings.capHeight,
                boxSize,
                capHeightInBox,
                boxMargin);
            Quaternion rotation = faceBackward ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
            string label = WaxGlyphAssets.Describe(Codepoint);
            glyphObject = CreatePart($"Glyph {label}", entry.Mesh, entry.Material, new Vector3(0f, lift * scale, 0f), rotation, scale);
            if (hasPedestal)
            {
                pedestalObject = CreatePart($"Pedestal {label}", entry.PedestalMesh, entry.PedestalMaterial, Vector3.zero, rotation, scale);
            }
        }

        private GameObject CreatePart(string partName, Mesh mesh, Material material, Vector3 position, Quaternion rotation, float scale)
        {
            var part = new GameObject(partName);
            part.transform.SetParent(transform, false);
            part.transform.localPosition = position;
            part.transform.localRotation = rotation;
            part.transform.localScale = Vector3.one * scale;
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var partRenderer = part.AddComponent<MeshRenderer>();
            partRenderer.sharedMaterial = material;
            partRenderer.SetShaderUserValue(FlowStartUserValue(flowStartTime));
            return part;
        }

        private void DestroyParts()
        {
            if (glyphObject != null)
            {
                Destroy(glyphObject);
                glyphObject = null;
            }

            if (pedestalObject != null)
            {
                Destroy(pedestalObject);
                pedestalObject = null;
            }
        }

        private bool TryGetCharacterSet(out GlyphCharacterSet set)
        {
            if (parsedSet == null || parsedSet.Pattern != characterSet)
            {
                try
                {
                    parsedSet = GlyphCharacterSet.Parse(characterSet);
                }
                catch (Exception exception) when (exception is FormatException || exception is ArgumentNullException)
                {
                    parsedSet = null;
                    Debug.LogError($"WaxGlyphBlock character set: {exception.Message}", this);
                }
            }

            set = parsedSet;
            return set != null;
        }
    }
}
