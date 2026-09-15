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
        private bool seedAssigned;

        /// <summary>The character picked by the last seed; -1 before one is assigned.</summary>
        public int Codepoint { get; private set; } = -1;

        /// <summary>The child that draws the glyph; null until its assets are ready.</summary>
        public GameObject GlyphObject => glyphObject;

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
            if (prewarmCharacterSet && set.Count <= MaxPrewarmCharacters)
            {
                foreach (int character in set.EnumerateCodepoints())
                {
                    WaxGlyphLibrary.Request(fontPath, character, buildSettings, template);
                }
            }

            if (glyphObject != null)
            {
                Destroy(glyphObject);
                glyphObject = null;
            }

            Codepoint = codepoint;
            entry = WaxGlyphLibrary.Request(fontPath, codepoint, buildSettings, template);
            enabled = true;
        }

        /// <summary>
        /// Uniform scale that makes a capital <paramref name="capHeightInBox"/> of the box
        /// height, reduced until a glyph with these bounds fits the box.
        /// </summary>
        /// <param name="letterBounds">Mesh XY bounds (xMin, yMin, xMax, yMax) of a glyph built with its bottom center at the origin.</param>
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

            glyphObject = new GameObject($"Glyph {WaxGlyphAssets.Describe(Codepoint)}");
            glyphObject.transform.SetParent(transform, false);
            glyphObject.transform.localRotation = faceBackward ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
            glyphObject.transform.localScale = Vector3.one * FitScale(
                entry.LetterBounds,
                entry.HalfDepth,
                settings.capHeight,
                boxSize,
                capHeightInBox,
                boxMargin);
            glyphObject.AddComponent<MeshFilter>().sharedMesh = entry.Mesh;
            glyphObject.AddComponent<MeshRenderer>().sharedMaterial = entry.Material;
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
