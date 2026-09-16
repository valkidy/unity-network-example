using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    /// <summary>
    /// Everything one burst needs to know at the moment a model comes apart.
    /// </summary>
    /// <remarks>
    /// Gathered into a struct for the same reason
    /// <see cref="NetworkSplatLaunch"/> is: the pool rolls most of this per
    /// burst, and a test that wants one exact burst has to be able to state it
    /// without the randomness in the way.
    ///
    /// The push is described rather than aimed. Nothing here says which way any
    /// one piece goes: the view knows where each piece sits inside the model, and
    /// that -- not a direction handed in -- is what sends a piece outwards.
    /// </remarks>
    public struct NetworkShatterLaunch
    {
        /// <summary>
        /// Where the push comes from, as a height through the model's own
        /// bounds: 0 is the ground it stands on, 1 is the top. Low puts the blast
        /// under the model and throws the whole thing up and out.
        /// </summary>
        public float blastHeightFraction;

        /// <summary>Speed given to the piece nearest the blast.</summary>
        public float nearSpeed;

        /// <summary>Speed given to the piece furthest from it.</summary>
        public float farSpeed;

        /// <summary>
        /// How much of the push is straight up rather than outwards, before the
        /// direction is normalized. Zero throws a wall of pieces sideways.
        /// </summary>
        public float upwardBias;

        /// <summary>
        /// How far a piece's speed is rolled either side of what its distance
        /// asks for, as a fraction of that speed. Zero makes every piece at the
        /// same distance move at the same speed, which reads as a shockwave.
        /// </summary>
        public float speedJitter;

        /// <summary>
        /// How much the largest piece is slowed, as a fraction of its speed. The
        /// smallest piece is never slowed, so 0.6 means the roof leaves at 40% of
        /// what a shingle gets.
        /// </summary>
        public float heavyPieceDamping;

        /// <summary>Fastest tumble a piece is given.</summary>
        public float maxSpinDegreesPerSecond;

        /// <summary>
        /// Pieces resting less than this above <see cref="floorY"/> stay where
        /// they are. The bottom of a model is the ground it is standing on --
        /// dirt, rock, a base -- and throwing that around gives away that the
        /// whole thing was one model.
        /// </summary>
        public float groundedHeight;

        /// <summary>World height a falling piece comes to rest on.</summary>
        public float floorY;

        /// <summary>Acceleration applied over the whole burst.</summary>
        public Vector3 gravity;

        /// <summary>Seconds from the burst to the last piece being gone.</summary>
        public float lifeSeconds;

        /// <summary>
        /// Seconds at the end of that life spent sinking out of sight. Clamped to
        /// <see cref="lifeSeconds"/>.
        /// </summary>
        public float sinkSeconds;

        /// <summary>
        /// Picks the burst. Every client that seeds from the same value sees the
        /// same pieces take the same paths.
        /// </summary>
        public int seed;
    }
}
