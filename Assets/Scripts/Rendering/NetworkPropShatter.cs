using System.Collections.Generic;
using NetworkExample.UnityDemo.Text3D;
using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    /// <summary>
    /// Blows a prop's model apart where it stood, and pools what it throws.
    /// </summary>
    /// <remarks>
    /// The trigger is deliberately not a despawn, in the same way
    /// <see cref="NetworkHitSplatters"/>'s is not a death:
    /// <see cref="TryShatter"/> takes a pose and a seed and nothing else, so
    /// whatever decides a nest has come down -- a lifecycle event, a test, a
    /// debug key -- can raise the effect without this component learning what a
    /// nest is.
    ///
    /// The seed is what keeps the effect the same on every client. Nothing about
    /// the debris is replicated and nothing needs to be: given the same seed, two
    /// clients solve the same arcs for the same pieces. The entity's net id is
    /// the seed every client already agrees on, which is the trick
    /// <see cref="GlyphCharacterSet"/> uses to put the same character on a
    /// glyph block everywhere.
    ///
    /// Pooling follows the splatters': one ceiling on live instances, and a full
    /// pool reuses its oldest rather than growing. An instance is a whole model's
    /// worth of renderers -- a hundred for the tower -- so the ceiling is low on
    /// purpose. Three nests cannot come down more than three at a time.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class NetworkPropShatter : MonoBehaviour
    {
        /// <summary>
        /// Where the shattered model is looked for when the field is left empty.
        /// </summary>
        public const string DefaultShatteredPrefabResourcePath =
            "Props/tower/tower-shattered";

        [SerializeField]
        [Tooltip(
            "The shattered model this throws, as baked by Network Example/" +
            "Presentation/Build Tower Shatter Assets. Left empty, " +
            "Resources/" + DefaultShatteredPrefabResourcePath + " is used.")]
        private GameObject shatteredPrefab;

        [SerializeField]
        [Tooltip("Parent for pooled instances. Left empty, this transform is used.")]
        private Transform shatterRoot;

        [SerializeField]
        [Range(0f, 1f)]
        [Tooltip(
            "Height through the model the blast pushes from, as a fraction of " +
            "what it reaches above the ground. Low throws the whole model up " +
            "and out; high drops the base and scatters the top.")]
        private float blastHeightFraction = 0.25f;

        [SerializeField]
        [Min(0f)]
        [Tooltip("Speed given to the piece nearest the blast.")]
        private float nearSpeed = 8.5f;

        [SerializeField]
        [Min(0f)]
        [Tooltip(
            "Speed given to the piece furthest from it. Below the near speed, " +
            "the model bursts from the inside; above it, the outside leaves first.")]
        private float farSpeed = 4.5f;

        [SerializeField]
        [Min(0f)]
        [Tooltip(
            "How much of the push is straight up rather than outwards. Zero " +
            "sweeps the pieces sideways along the ground.")]
        private float upwardBias = 0.7f;

        [SerializeField]
        [Range(0f, 1f)]
        [Tooltip(
            "How far a piece's speed is rolled either side of what its distance " +
            "asks for. Zero makes the burst read as one clean shockwave.")]
        private float speedJitter = 0.35f;

        [SerializeField]
        [Range(0f, 1f)]
        [Tooltip(
            "How much the largest piece is held back, so a roof does not leave " +
            "like a shingle. Size stands in for the mass a hollow model has none of.")]
        private float heavyPieceDamping = 0.35f;

        [SerializeField]
        [Min(0f)]
        [Tooltip("Fastest tumble a piece is given.")]
        private float maxSpinDegreesPerSecond = 360f;

        [SerializeField]
        [Min(0f)]
        [Tooltip(
            "Pieces resting less than this above the prop's feet stay where they " +
            "are: the dirt and rock a model stands on give the whole trick away " +
            "if they fly.")]
        private float groundedHeight = 0.6f;

        [SerializeField]
        [Min(0.1f)]
        [Tooltip("Seconds from the burst to the last piece being gone.")]
        private float lifeSeconds = 3.5f;

        [SerializeField]
        [Min(0f)]
        [Tooltip(
            "Seconds at the end of that life spent sinking through the ground, " +
            "so the debris leaves instead of blinking out. Clamped to the life.")]
        private float sinkSeconds = 1f;

        [SerializeField]
        [Min(1)]
        [Tooltip("Ceiling on pooled instances. A full pool reuses the oldest burst.")]
        private int maxLiveBursts = 3;

        private readonly List<Burst> bursts = new List<Burst>();
        private GameObject resolvedPrefab;
        private bool prefabResolved;

        public int PooledBurstCount => bursts.Count;

        public int LiveBurstCount
        {
            get
            {
                int live = 0;
                for (int index = 0; index < bursts.Count; ++index)
                {
                    if (bursts[index].view != null && bursts[index].view.IsPlaying)
                    {
                        ++live;
                    }
                }

                return live;
            }
        }

        public void Configure(Transform root)
        {
            shatterRoot = root;
        }

        public void Configure(Transform root, GameObject prefab)
        {
            shatterRoot = root;
            shatteredPrefab = prefab;
            prefabResolved = false;
            resolvedPrefab = null;
        }

        /// <summary>
        /// Blows the model apart standing at <paramref name="position"/> and
        /// <paramref name="rotation"/>. Returns false when there is no model to
        /// throw, which is what a project without the bake produces.
        /// </summary>
        /// <remarks>
        /// The pose is the prop's own, so the pieces start exactly where that
        /// prop's renderer was drawing them, and the ground they land on is the
        /// height of its feet.
        /// </remarks>
        public bool TryShatter(Vector3 position, Quaternion rotation, ulong seed)
        {
            return TryShatter(null, position, rotation, seed);
        }

        /// <summary>
        /// Blows <paramref name="model"/> apart at that pose. A null model uses
        /// whatever this has bound as its default.
        /// </summary>
        /// <remarks>
        /// Each model is pooled on its own, up to the same ceiling, so a second
        /// kind of breakable prop cannot starve the first of instances -- and an
        /// instance built from one model is never handed out for another, which
        /// would draw the wrong wreckage.
        /// </remarks>
        public bool TryShatter(
            GameObject model,
            Vector3 position,
            Quaternion rotation,
            ulong seed)
        {
            GameObject resolved = model != null ? model : ResolvePrefab();
            if (resolved == null)
            {
                return false;
            }

            NetworkShatterView burst = Rent(resolved);
            if (burst == null)
            {
                return false;
            }

            // Posed before the burst is solved: the arcs are read off where the
            // pieces stand, so an instance still sitting where it last played
            // would throw the debris from there.
            burst.transform.SetPositionAndRotation(position, rotation);
            burst.Play(BuildLaunch(position.y, Mix(seed)));
            return burst.IsPlaying;
        }

        private void Update()
        {
            Tick(Time.unscaledDeltaTime);
        }

        /// <summary>
        /// Ages every live burst. Driven from Update in a running session and
        /// callable directly from a test, where nothing calls Update at all.
        /// </summary>
        public void Tick(float deltaSeconds)
        {
            for (int index = 0; index < bursts.Count; ++index)
            {
                NetworkShatterView burst = bursts[index].view;
                if (burst != null)
                {
                    burst.Tick(deltaSeconds);
                }
            }
        }

        private NetworkShatterLaunch BuildLaunch(float floorY, int seed)
        {
            return new NetworkShatterLaunch
            {
                blastHeightFraction = blastHeightFraction,
                nearSpeed = nearSpeed,
                farSpeed = farSpeed,
                upwardBias = upwardBias,
                speedJitter = speedJitter,
                heavyPieceDamping = heavyPieceDamping,
                maxSpinDegreesPerSecond = maxSpinDegreesPerSecond,
                groundedHeight = groundedHeight,
                floorY = floorY,
                gravity = Physics.gravity,
                lifeSeconds = Mathf.Max(0.1f, lifeSeconds),
                sinkSeconds = sinkSeconds,
                seed = seed,
            };
        }

        private NetworkShatterView Rent(GameObject model)
        {
            int built = 0;
            for (int index = 0; index < bursts.Count; ++index)
            {
                Burst candidate = bursts[index];
                if (candidate.model != model || candidate.view == null)
                {
                    continue;
                }

                ++built;
                if (!candidate.view.IsPlaying)
                {
                    return candidate.view;
                }
            }

            if (built < Mathf.Max(1, maxLiveBursts))
            {
                NetworkShatterView created = Create(model);
                if (created != null)
                {
                    bursts.Add(new Burst(created, model));
                }

                return created;
            }

            for (int index = 0; index < bursts.Count; ++index)
            {
                Burst oldest = bursts[index];
                if (oldest.model != model || oldest.view == null)
                {
                    continue;
                }

                // Every instance of this model is still on screen. The oldest
                // burst is the one closest to being over anyway, and taking it
                // keeps a wave of destruction from instantiating models without a
                // ceiling. Rotating it to the back is what keeps "the first one
                // found is the oldest" true for the next call.
                bursts.RemoveAt(index);
                bursts.Add(oldest);
                oldest.view.Stop();
                return oldest.view;
            }

            return null;
        }

        private NetworkShatterView Create(GameObject prefab)
        {
            if (prefab == null)
            {
                return null;
            }

            GameObject instance = Instantiate(
                prefab,
                shatterRoot != null ? shatterRoot : transform,
                false);
            instance.name = prefab.name + " (burst)";
            // Nothing about a burst is simulated, so anything that would push a
            // piece off its arc -- or report it to gameplay -- is stripped, the
            // same way a splat's body is.
            Collider[] colliders = instance.GetComponentsInChildren<Collider>(true);
            for (int index = 0; index < colliders.Length; ++index)
            {
                DestroyNow(colliders[index]);
            }

            Rigidbody[] bodies = instance.GetComponentsInChildren<Rigidbody>(true);
            for (int index = 0; index < bodies.Length; ++index)
            {
                DestroyNow(bodies[index]);
            }

            NetworkShatterView view = instance.GetComponent<NetworkShatterView>();
            if (view == null)
            {
                view = instance.AddComponent<NetworkShatterView>();
            }

            // Bound while the pieces still stand in the model's own pose, because
            // that pose is what every burst afterwards is thrown from.
            if (view.Bind() == 0)
            {
                DestroyNow(instance);
                return null;
            }

            view.Stop();
            return view;
        }

        private GameObject ResolvePrefab()
        {
            if (shatteredPrefab != null)
            {
                return shatteredPrefab;
            }

            if (!prefabResolved)
            {
                prefabResolved = true;
                resolvedPrefab = Resources.Load<GameObject>(
                    DefaultShatteredPrefabResourcePath);
            }

            return resolvedPrefab;
        }

        /// <summary>
        /// Spreads a net id out into a seed.
        /// </summary>
        /// <remarks>
        /// Net ids are handed out in order, so seeding straight from one would
        /// give the nests of a single match neighbouring seeds -- and
        /// neighbouring seeds are where a generator's first values look alike.
        /// The scattering is <see cref="GlyphCharacterSet.Mix"/>, which is there
        /// for exactly this and for the same reason: two clients agreeing on
        /// what a prop looks like without either being told.
        /// </remarks>
        private static int Mix(ulong seed)
        {
            return (int)(GlyphCharacterSet.Mix(seed) & 0x7FFFFFFFUL);
        }

        /// <summary>One pooled instance, and the model it was built from.</summary>
        private readonly struct Burst
        {
            public readonly NetworkShatterView view;
            public readonly GameObject model;

            public Burst(NetworkShatterView view, GameObject model)
            {
                this.view = view;
                this.model = model;
            }
        }

        private static void DestroyNow(Object target)
        {
            if (Application.isPlaying)
            {
                Destroy(target);
            }
            else
            {
                DestroyImmediate(target);
            }
        }
    }
}
