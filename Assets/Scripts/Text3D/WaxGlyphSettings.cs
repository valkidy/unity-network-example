using System;
using UnityEngine;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>
    /// How a character becomes a WaxCandy glyph mesh and edge distance map.
    /// </summary>
    /// <remarks>
    /// Lengths are glyph object units. WaxCandy V7's widths and noise scales are tuned on
    /// a reference letter about 1.9 units tall and 0.4 deep, so the defaults build glyphs
    /// at that size; scale the text's transform for its size in the world.
    /// </remarks>
    [Serializable]
    public struct WaxGlyphSettings
    {
        [Tooltip("Height of a capital letter in glyph object units. WaxCandy V7 is tuned on a letter 1.9 units tall.")]
        public float capHeight;

        [Tooltip("Triangle ceiling for one character, caps and walls together.")]
        public int triangleBudget;

        [Tooltip("Half the extrusion depth. Written to the material's _HalfDepth.")]
        public float halfDepth;

        [Tooltip("Most wall bands across the depth. The WaxCandy reference letter uses 14.")]
        public int maxWallSegments;

        [Tooltip("Fewest wall bands. Below this the outline is simplified to fit the budget instead.")]
        public int minWallSegments;

        [Tooltip("How far the middle of the wall bulges past the silhouette.")]
        public float wallBulge;

        [Tooltip("Horizontal half-axis of the ellipse the wall normals follow; larger reads rounder.")]
        public float wallNormalRoundness;

        [Tooltip("Largest distance a flattened curve may stray from the font's curve.")]
        public float curveTolerance;

        [Tooltip("Radius used to round sharp outline corners so the smooth wall normals stay smooth.")]
        public float cornerRadius;

        [Tooltip("Largest turn one segment of a rounded corner may cover. CJK glyphs have many corners, so " +
                 "coarser arcs leave more of the budget for wall bands. 0 or less uses 45.")]
        public float cornerArcStepDegrees;

        [Tooltip("Edge distance map resolution in texels per object unit.")]
        public float mapTexelsPerUnit;

        [Tooltip("Empty border around the glyph in the edge distance map.")]
        public float mapPadding;

        [Tooltip("Distance stored at full intensity. Written to the material's _DistanceRange.")]
        public float mapDistanceRange;

        /// <remarks>
        /// The wall profile comes from the WaxCandy reference letter: its rings bulge at
        /// most about 0.0135 past the silhouette, and its normals match an ellipse whose
        /// horizontal half-axis is 0.025 against the 0.2 half depth.
        ///
        /// The rest favours triangles and build time. Measured on Noto Sans TC Bold, six
        /// wall bands (the reference letter uses 14), 45-degree corner arcs and a 0.002
        /// curve tolerance average about 970 triangles per Latin glyph and 2,900 per CJK
        /// glyph, against 3,050 and 4,450 before; a 192 texels-per-unit map halves the
        /// bake time of a 256 one.
        /// </remarks>
        public static WaxGlyphSettings Default => new WaxGlyphSettings
        {
            capHeight = 1.9f,
            triangleBudget = 5000,
            halfDepth = 0.2f,
            maxWallSegments = 6,
            minWallSegments = 2,
            wallBulge = 0.0135f,
            wallNormalRoundness = 0.025f,
            curveTolerance = 0.002f,
            cornerRadius = 0.025f,
            cornerArcStepDegrees = 45f,
            mapTexelsPerUnit = 192f,
            mapPadding = 0.06f,
            mapDistanceRange = 0.5f,
        };
    }
}
