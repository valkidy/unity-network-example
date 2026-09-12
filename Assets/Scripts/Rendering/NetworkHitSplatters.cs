using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace NetworkExample.UnityDemo.Rendering
{
    /// <summary>
    /// Throws a handful of splatters onto the ground under a point, and pools
    /// what it throws.
    /// </summary>
    /// <remarks>
    /// The trigger is deliberately not death. A splatter is a mark left where
    /// something violent happened at a world position, and death is only the
    /// first caller: <see cref="TrySplat"/> takes a point and nothing else, so a
    /// hit, an impact or a burst can raise the same effect without this component
    /// learning what any of them are.
    ///
    /// Landing points come from a Unity raycast, not from the kernel. The kernel
    /// owns collision for everything the simulation resolves, but it exposes no
    /// query to ask where the ground is -- nothing in its ABI reads geometry back
    /// out -- and it has no terrain loaded on a client in the first place. So this
    /// is presentation's own approximation of the ground, in the same spirit as
    /// <see cref="NetworkHitscanTracers.clipAgainstColliders"/>, and it needs the
    /// scene to actually carry a collider for the ground it draws on.
    ///
    /// Pooling follows the tracers': one ceiling on live instances, and a full
    /// pool reuses its oldest rather than growing. A splat lives for seconds
    /// rather than a tracer's fraction of one, so the ceiling is the difference
    /// between a busy fight and an unbounded pile of projectors.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class NetworkHitSplatters : MonoBehaviour
    {
        /// <summary>
        /// Where the splat material is looked for when the field is left empty.
        /// </summary>
        public const string DefaultSplatMaterialResourcePath = "Decals/Decals";

        [SerializeField]
        [Tooltip(
            "The URP decal material every splat projects. Left empty, " +
            "Resources/Decals/Decals is used. Without one the splats still fly " +
            "and expire, they just leave no mark.")]
        private Material splatMaterial;

        [SerializeField]
        [Tooltip("Parent for pooled splat instances. Left empty, this transform is used.")]
        private Transform splatRoot;

        [SerializeField]
        [Tooltip(
            "Art for the airborne half of a splat. Left empty, a small cube is " +
            "built instead. Any collider on it is stripped: a splat is drawn, " +
            "not simulated.")]
        private GameObject flightPrefab;

        [SerializeField]
        [Tooltip(
            "Tints the cube built when no flight prefab is bound. Ignored once " +
            "a prefab is, since that prefab authors its own look.")]
        private Color flightColor = new Color(0.32f, 0.05f, 0.07f, 1f);

        [SerializeField]
        [Min(1)]
        [Tooltip("Fewest splats one call throws.")]
        private int minSplats = 3;

        [SerializeField]
        [Min(1)]
        [Tooltip("Most splats one call throws.")]
        private int maxSplats = 5;

        [SerializeField]
        [Min(0f)]
        [Tooltip("How far from the point splats scatter before they are dropped.")]
        private float scatterRadius = 1.2f;

        [SerializeField]
        [Min(0f)]
        [Tooltip(
            "Height above the point splats launch from. The kernel places an " +
            "actor's origin on its feet, so leaving this near 1 throws from the " +
            "body rather than the ground.")]
        private float launchHeight = 1f;

        [SerializeField]
        [Min(0.01f)]
        [Tooltip("Seconds from launch to landing. Longer lobs the arc higher.")]
        private float flightSeconds = 0.2f;

        [SerializeField]
        [Min(0f)]
        [Tooltip("Shortest time a landed splat stays.")]
        private float minSettledSeconds = 3f;

        [SerializeField]
        [Min(0f)]
        [Tooltip("Longest time a landed splat stays.")]
        private float maxSettledSeconds = 5f;

        [SerializeField]
        [Min(0f)]
        [Tooltip(
            "Seconds of fade at the end of a splat's life, so it thins out " +
            "instead of blinking off. Clamped to the settled time.")]
        private float fadeSeconds = 0.75f;

        [SerializeField]
        [Tooltip("Footprint of a landed splat on the surface, picked per splat.")]
        private Vector2 splatSizeRange = new Vector2(1.7f, 2.5f);

        [SerializeField]
        [Min(0.01f)]
        [Tooltip(
            "How far a splat's projection reaches through the surface. Too " +
            "shallow and it clips off uneven ground; too deep and it wraps onto " +
            "things standing on that ground.")]
        private float splatProjectionDepth = 1.5f;

        [SerializeField]
        [Tooltip(
            "Surfaces a splat can land on. Narrow this to the ground: anything " +
            "with a collider is otherwise a candidate, including actors.")]
        private LayerMask groundLayers = ~0;

        [SerializeField]
        [Min(0f)]
        [Tooltip("How far above the point the ground probe starts.")]
        private float groundProbeHeight = 8f;

        [SerializeField]
        [Min(0f)]
        [Tooltip("How far below the point the ground probe still looks.")]
        private float groundProbeDepth = 40f;

        [SerializeField]
        [Min(0f)]
        [Tooltip(
            "Lifts a landed decal off the surface it projects onto, so a splat " +
            "on a slope does not fight the ground for the same depth.")]
        private float groundOffset = 0.02f;

        [SerializeField]
        [Min(0f)]
        [Tooltip("Degrees per second an airborne splat tumbles.")]
        private float maxSpinDegreesPerSecond = 240f;

        [SerializeField]
        [Min(1)]
        [Tooltip("Ceiling on pooled instances. A full pool reuses the oldest splat.")]
        private int maxLiveSplats = 64;

        private readonly List<NetworkSplatView> splats = new List<NetworkSplatView>();
        private readonly List<GroundHit> probed = new List<GroundHit>();
        private Material flightMaterial;
        private Material resolvedSplatMaterial;
        private bool splatMaterialResolved;

        public int PooledSplatCount => splats.Count;

        public int LiveSplatCount
        {
            get
            {
                int live = 0;
                for (int index = 0; index < splats.Count; ++index)
                {
                    if (splats[index] != null && splats[index].IsPlaying)
                    {
                        ++live;
                    }
                }
                return live;
            }
        }

        public void Configure(Transform root)
        {
            splatRoot = root;
        }

        public void Configure(Transform root, Material material)
        {
            splatRoot = root;
            splatMaterial = material;
            splatMaterialResolved = false;
            resolvedSplatMaterial = null;
        }

        /// <summary>
        /// Throws a burst of splats onto the ground under <paramref name="origin"/>.
        /// Returns false when the probe found nothing to land on, which is what a
        /// scene with no ground collider produces.
        /// </summary>
        public bool TrySplat(Vector3 origin)
        {
            int wanted = Random.Range(
                Mathf.Max(1, minSplats),
                Mathf.Max(minSplats, maxSplats) + 1);

            // Every probe runs before the first instance is built, rather than
            // one probe per spawn. Building a splat strips the collider off its
            // body, and in play mode that destruction does not take effect until
            // the end of the frame -- so interleaving the two would let the
            // second splat of a burst land on the body of the first.
            probed.Clear();
            for (int index = 0; index < wanted; ++index)
            {
                if (TryProbeGround(origin, out Vector3 landing, out Vector3 normal))
                {
                    // A splat whose scatter fell off the edge of the ground is
                    // dropped, not nudged back: the rest of the burst still lands.
                    probed.Add(new GroundHit(landing, normal));
                }
            }

            Vector3 launchFrom = origin + (Vector3.up * launchHeight);
            int thrown = 0;
            for (int index = 0; index < probed.Count; ++index)
            {
                NetworkSplatView splat = Rent();
                if (splat == null)
                {
                    break;
                }

                splat.Play(BuildLaunch(launchFrom, probed[index].point, probed[index].normal));
                ++thrown;
            }

            probed.Clear();
            return thrown > 0;
        }

        private void Update()
        {
            Tick(Time.unscaledDeltaTime);
        }

        /// <summary>
        /// Ages every live splat. Driven from Update in a running session and
        /// callable directly from a test, where nothing calls Update at all.
        /// </summary>
        public void Tick(float deltaSeconds)
        {
            for (int index = 0; index < splats.Count; ++index)
            {
                NetworkSplatView splat = splats[index];
                if (splat != null)
                {
                    splat.Tick(deltaSeconds);
                }
            }
        }

        private NetworkSplatLaunch BuildLaunch(
            Vector3 launchFrom,
            Vector3 landing,
            Vector3 normal)
        {
            float footprint = Random.Range(
                Mathf.Min(splatSizeRange.x, splatSizeRange.y),
                Mathf.Max(splatSizeRange.x, splatSizeRange.y));
            float settled = Random.Range(
                Mathf.Min(minSettledSeconds, maxSettledSeconds),
                Mathf.Max(minSettledSeconds, maxSettledSeconds));

            return new NetworkSplatLaunch
            {
                origin = launchFrom,
                landing = landing + (normal * groundOffset),
                landingNormal = normal,
                gravity = Physics.gravity,
                flightSeconds = Mathf.Max(0.01f, flightSeconds),
                settledSeconds = Mathf.Max(0.01f, settled),
                fadeSeconds = fadeSeconds,
                decalRollDegrees = Random.Range(0f, 360f),
                decalSize = new Vector3(
                    footprint,
                    footprint,
                    Mathf.Max(0.01f, splatProjectionDepth)),
                spinDegreesPerSecond = Random.insideUnitSphere * maxSpinDegreesPerSecond,
            };
        }

        private bool TryProbeGround(Vector3 origin, out Vector3 landing, out Vector3 normal)
        {
            Vector2 scatter = Random.insideUnitCircle * scatterRadius;
            Vector3 target = new Vector3(
                origin.x + scatter.x,
                origin.y,
                origin.z + scatter.y);
            Vector3 probeStart = target + (Vector3.up * groundProbeHeight);

            if (Physics.Raycast(
                    probeStart,
                    Vector3.down,
                    out RaycastHit hit,
                    groundProbeHeight + groundProbeDepth,
                    groundLayers,
                    QueryTriggerInteraction.Ignore))
            {
                landing = hit.point;
                normal = hit.normal;
                return true;
            }

            landing = default;
            normal = default;
            return false;
        }

        private NetworkSplatView Rent()
        {
            for (int index = 0; index < splats.Count; ++index)
            {
                NetworkSplatView candidate = splats[index];
                if (candidate != null && !candidate.IsPlaying)
                {
                    return candidate;
                }
            }

            if (splats.Count >= Mathf.Max(1, maxLiveSplats))
            {
                // Every instance is still on screen. The oldest splat is the one
                // closest to fading out anyway, and taking it keeps a heavy fight
                // from allocating projectors without a ceiling. Rotating it to the
                // back is what keeps "index 0 is oldest" true for the next call.
                NetworkSplatView oldest = splats[0];
                if (oldest == null)
                {
                    return null;
                }

                splats.RemoveAt(0);
                splats.Add(oldest);
                oldest.Stop();
                return oldest;
            }

            NetworkSplatView created = Create();
            if (created != null)
            {
                splats.Add(created);
            }
            return created;
        }

        private NetworkSplatView Create()
        {
            var root = new GameObject("Splat");
            // Deactivated before anything is built under it, and it stays that
            // way until a launch poses it: a child instantiated into an inactive
            // parent does not wake up at all, so a flight prefab carrying a
            // world-space trail or particle system runs its Awake at the throw
            // rather than wherever the pool happened to build it.
            root.SetActive(false);
            root.transform.SetParent(splatRoot != null ? splatRoot : transform, false);

            NetworkSplatView view = root.AddComponent<NetworkSplatView>();
            Transform body = CreateFlightBody(root.transform);
            view.Bind(body, CreateProjector(root));
            view.Stop();
            return view;
        }

        private DecalProjector CreateProjector(GameObject root)
        {
            Material material = ResolveSplatMaterial();
            if (material == null)
            {
                // No art, no projector. The splat still flies and expires, which
                // keeps the pool's behaviour testable without a render pipeline.
                return null;
            }

            DecalProjector projector = root.AddComponent<DecalProjector>();
            projector.material = material;
            projector.pivot = Vector3.zero;
            projector.enabled = false;
            return projector;
        }

        private Transform CreateFlightBody(Transform parent)
        {
            // Parented as it is created, with instantiateInWorldSpace false, which
            // lands the clone on the prefab's authored local pose exactly as
            // SetParent(parent, false) did -- without the moment spent unparented
            // at the world origin.
            GameObject body = flightPrefab != null
                ? Instantiate(flightPrefab, parent, false)
                : GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "SplatBody";
            if (flightPrefab == null)
            {
                body.transform.SetParent(parent, false);
            }

            // A splat is thrown along a solved arc, so anything that would push it
            // off that arc -- or that would report a hit to gameplay -- is removed.
            Collider[] colliders = body.GetComponentsInChildren<Collider>(true);
            for (int index = 0; index < colliders.Length; ++index)
            {
                DestroyNow(colliders[index]);
            }

            Rigidbody[] bodies = body.GetComponentsInChildren<Rigidbody>(true);
            for (int index = 0; index < bodies.Length; ++index)
            {
                DestroyNow(bodies[index]);
            }

            if (flightPrefab == null)
            {
                body.transform.localScale = Vector3.one * 0.15f;
                Renderer renderer = body.GetComponent<Renderer>();
                if (renderer != null)
                {
                    // sharedMaterial with one instance of our own, rather than
                    // Renderer.material: that property instantiates per renderer,
                    // and outside play mode it leaks the copy into the scene and
                    // logs an error for doing so.
                    renderer.sharedMaterial =
                        EnsureFlightMaterial(renderer.sharedMaterial);
                }
            }

            return body.transform;
        }

        private Material EnsureFlightMaterial(Material source)
        {
            if (flightMaterial == null && source != null)
            {
                flightMaterial = new Material(source)
                {
                    name = "SplatFlight",
                    color = flightColor,
                };
            }

            return flightMaterial != null ? flightMaterial : source;
        }

        private Material ResolveSplatMaterial()
        {
            if (splatMaterial != null)
            {
                return splatMaterial;
            }

            if (!splatMaterialResolved)
            {
                splatMaterialResolved = true;
                resolvedSplatMaterial = Resources.Load<Material>(
                    DefaultSplatMaterialResourcePath);
            }

            return resolvedSplatMaterial;
        }

        private readonly struct GroundHit
        {
            public readonly Vector3 point;
            public readonly Vector3 normal;

            public GroundHit(Vector3 point, Vector3 normal)
            {
                this.point = point;
                this.normal = normal;
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
