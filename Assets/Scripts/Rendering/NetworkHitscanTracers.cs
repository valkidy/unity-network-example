using System.Collections.Generic;
using NetworkExample.UnityDemo.Common;
using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    /// <summary>
    /// Draws a shot for the weapons that spawn nothing to draw: hitscan and
    /// shotgun.
    /// </summary>
    /// <remarks>
    /// Every other weapon reaches a client as an entity -- a projectile with a
    /// position, or a beam with a replicated endpoint -- and
    /// <see cref="NetworkRenderStateApplier"/> gives it a prefab off the render
    /// state. An instant weapon has no entity at all. The segment collider the
    /// server materializes for it is not an answer either: it is never predicted
    /// on a client, and the kernel rebuilds a client's collider registry from
    /// render states every tick, so a client-mode session never sees one.
    ///
    /// What does arrive is the action commit -- a FireCommit presentation event
    /// for a remote actor, and a local action result's confirmed commit count for
    /// the local player -- which is one signal per shot on both sides. This turns
    /// each of those into a line: the shooter's replicated position and aim give
    /// the ray, and the catalog gives its reach, its pellet pattern and the
    /// projectile template whose prefab is its art.
    ///
    /// The line is drawn at full reach, because that is the shot the server
    /// actually resolved: it does not truncate its own segment at whatever it hit.
    /// Clipping is offered as presentation (<see cref="clipAgainstColliders"/>)
    /// and is off by default, since collision in this project belongs to the
    /// kernel and a scene need not carry Unity colliders at all.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class NetworkHitscanTracers : MonoBehaviour
    {
        [SerializeField]
        [Tooltip(
            "Resolves tracer art through the same projectile-template binding " +
            "every other weapon's prefab comes from. Left empty, a procedural " +
            "line is used instead.")]
        private NetworkPrefabRegistry prefabRegistry;

        [SerializeField]
        [Tooltip("Parent for pooled tracer instances. Left empty, this transform is used.")]
        private Transform tracerRoot;

        [SerializeField]
        [Tooltip(
            "Overrides the catalog for every instant weapon. Must follow the " +
            "beam prefab convention: a body mesh along local +Z, two units long, " +
            "near end on the root.")]
        private GameObject tracerPrefabOverride;

        [SerializeField]
        [Min(0f)]
        [Tooltip(
            "How long one shot stays on screen. The kernel keeps its own hitscan " +
            "segment for 3 ticks (0.1s at 30Hz); a tracer that short is easy to " +
            "miss, so this is presentation and not required to match.")]
        private float tracerSeconds = 0.08f;

        [SerializeField]
        [Min(0f)]
        [Tooltip(
            "Muzzle height above the actor's replicated position. The kernel " +
            "fires from position + (0, 1, 0) -- see projectile_launch_position -- " +
            "so leaving this at 1 puts the tracer on the ray the server resolved.")]
        private float muzzleHeight = 1f;

        [SerializeField]
        [Tooltip(
            "Shortens a tracer at the first Unity collider it meets. Off by " +
            "default: collision is the kernel's, and a scene here may carry no " +
            "Unity colliders at all, in which case every shot draws full reach.")]
        private bool clipAgainstColliders;

        [SerializeField]
        private LayerMask clipLayers = ~0;

        [SerializeField]
        [Min(0.001f)]
        [Tooltip("Thickness of the procedural tracer used when no prefab is bound.")]
        private float fallbackGirth = 0.04f;

        [SerializeField]
        private Color fallbackColor = new Color(1f, 0.85f, 0.35f, 1f);

        [SerializeField]
        [Min(1)]
        [Tooltip("Ceiling on pooled instances. A full pool drops the oldest shot.")]
        private int maxLiveTracers = 64;

        private readonly Dictionary<uint, NetworkInstantWeaponPresentation> weaponsByFireAction =
            new Dictionary<uint, NetworkInstantWeaponPresentation>();
        private readonly Dictionary<uint, GameObject> prefabsByProjectileTemplate =
            new Dictionary<uint, GameObject>();
        // Pooled per projectile template, not in one pile: an instance is built
        // around the prefab its shot's art is bound to, so handing a shotgun
        // pellet an idle rifle tracer would draw the wrong weapon.
        private readonly Dictionary<uint, List<NetworkTracerView>> tracersByTemplate =
            new Dictionary<uint, List<NetworkTracerView>>();
        private readonly List<NetworkTracerView> tracers = new List<NetworkTracerView>();
        private Material fallbackMaterial;

        public int WeaponCount => weaponsByFireAction.Count;

        public int LiveTracerCount
        {
            get
            {
                int live = 0;
                for (int index = 0; index < tracers.Count; ++index)
                {
                    if (tracers[index] != null && tracers[index].IsPlaying)
                    {
                        ++live;
                    }
                }
                return live;
            }
        }

        public void Configure(NetworkPrefabRegistry prefabs, Transform root)
        {
            prefabRegistry = prefabs;
            tracerRoot = root;
        }

        /// <summary>
        /// Installs the instant weapons read out of the gameplay catalog. Called
        /// once the catalog the session will run is known -- the packaged bundle
        /// on a host, the synchronized one on a client -- so a weapon added to the
        /// catalog draws without a change here.
        /// </summary>
        public void ConfigureWeapons(
            Dictionary<uint, NetworkInstantWeaponPresentation> byFireActionTemplateId)
        {
            weaponsByFireAction.Clear();
            prefabsByProjectileTemplate.Clear();
            // Pooled instances outlive the table only if the art behind them
            // still applies, and a new table can bind different art.
            for (int index = 0; index < tracers.Count; ++index)
            {
                if (tracers[index] != null)
                {
                    DestroyNow(tracers[index].gameObject);
                }
            }
            tracers.Clear();
            tracersByTemplate.Clear();
            if (byFireActionTemplateId == null)
            {
                return;
            }

            foreach (KeyValuePair<uint, NetworkInstantWeaponPresentation> pair in
                byFireActionTemplateId)
            {
                weaponsByFireAction[pair.Key] = pair.Value;
            }
        }

        /// <summary>
        /// Draws one commit of <paramref name="fireActionTemplateId"/>. Returns
        /// false for an action that is not an instant weapon, which is every
        /// projectile, beam, area effect and reload -- those draw themselves from
        /// their own entities.
        /// </summary>
        public bool TryFire(
            uint fireActionTemplateId,
            Vector3 shooterPosition,
            Vector3 aimDirection)
        {
            if (fireActionTemplateId == 0 ||
                aimDirection.sqrMagnitude <= 1e-8f ||
                !weaponsByFireAction.TryGetValue(
                    fireActionTemplateId,
                    out NetworkInstantWeaponPresentation weapon))
            {
                return false;
            }

            Vector3 origin = shooterPosition + Vector3.up * muzzleHeight;
            Vector3 aim = aimDirection.normalized;
            int pellets = Mathf.Max(1, weapon.PelletCount);
            for (int pellet = 0; pellet < pellets; ++pellet)
            {
                Vector3 direction = weapon.PelletDirection(aim, pellet);
                Play(origin, direction, ReachOf(weapon, origin, direction), weapon);
            }

            return true;
        }

        private void Update()
        {
            Tick(Time.unscaledDeltaTime);
        }

        /// <summary>
        /// Ages every live tracer. Driven from Update in a running session and
        /// callable directly from a test, where nothing calls Update at all.
        /// </summary>
        public void Tick(float deltaSeconds)
        {
            for (int index = 0; index < tracers.Count; ++index)
            {
                NetworkTracerView tracer = tracers[index];
                if (tracer != null)
                {
                    tracer.Tick(deltaSeconds);
                }
            }
        }

        private float ReachOf(
            NetworkInstantWeaponPresentation weapon,
            Vector3 origin,
            Vector3 direction)
        {
            float reach = weapon.MaxRange > 0f ? weapon.MaxRange : 1f;
            if (!clipAgainstColliders)
            {
                return reach;
            }

            return Physics.Raycast(
                origin,
                direction,
                out RaycastHit hit,
                reach,
                clipLayers,
                QueryTriggerInteraction.Ignore)
                ? hit.distance
                : reach;
        }

        private void Play(
            Vector3 origin,
            Vector3 direction,
            float reach,
            NetworkInstantWeaponPresentation weapon)
        {
            NetworkTracerView tracer = Rent(weapon.ProjectileTemplateId);
            if (tracer != null)
            {
                tracer.Play(origin, direction, reach, Mathf.Max(0.001f, tracerSeconds));
            }
        }

        private NetworkTracerView Rent(uint projectileTemplateId)
        {
            if (!tracersByTemplate.TryGetValue(
                    projectileTemplateId,
                    out List<NetworkTracerView> pool))
            {
                pool = new List<NetworkTracerView>();
                tracersByTemplate[projectileTemplateId] = pool;
            }

            for (int index = 0; index < pool.Count; ++index)
            {
                NetworkTracerView candidate = pool[index];
                if (candidate != null && !candidate.IsPlaying)
                {
                    return candidate;
                }
            }

            if (tracers.Count >= Mathf.Max(1, maxLiveTracers))
            {
                // Every instance is still on screen. The oldest shot of this
                // weapon is the one a viewer is least likely to still be looking
                // at, and reusing it keeps a held trigger from allocating without
                // a ceiling.
                NetworkTracerView oldest = pool.Count > 0 ? pool[0] : null;
                if (oldest == null)
                {
                    return null;
                }

                pool.RemoveAt(0);
                pool.Add(oldest);
                return oldest;
            }

            NetworkTracerView created = Create(projectileTemplateId);
            if (created != null)
            {
                tracers.Add(created);
                pool.Add(created);
            }
            return created;
        }

        private NetworkTracerView Create(uint projectileTemplateId)
        {
            GameObject prefab = ResolvePrefab(projectileTemplateId);
            GameObject instance = prefab != null
                ? Instantiate(prefab)
                : CreateProceduralTracer();
            instance.name = "Tracer";
            instance.transform.SetParent(
                tracerRoot != null ? tracerRoot : transform,
                false);

            // An authored beam prefab carries a NetworkProjectileView, which would
            // otherwise sit there waiting for a render state that never comes for
            // a tracer. The stretch it performs is the same one NetworkTracerView
            // performs, so the tracer keeps the mesh and drops the view.
            NetworkProjectileView projectileView =
                instance.GetComponent<NetworkProjectileView>();
            if (projectileView != null)
            {
                DestroyNow(projectileView);
            }

            NetworkTracerView tracer = instance.GetComponent<NetworkTracerView>();
            if (tracer == null)
            {
                tracer = instance.AddComponent<NetworkTracerView>();
            }

            tracer.Stop();
            return tracer;
        }

        private GameObject ResolvePrefab(uint projectileTemplateId)
        {
            if (tracerPrefabOverride != null)
            {
                return tracerPrefabOverride;
            }

            if (prefabsByProjectileTemplate.TryGetValue(
                    projectileTemplateId,
                    out GameObject cached))
            {
                return cached;
            }

            GameObject prefab = null;
            NetworkPrefabCatalog catalog =
                prefabRegistry != null ? prefabRegistry.Catalog : null;
            if (catalog != null && projectileTemplateId != 0)
            {
                // Only an exact binding. Falling back to the catalog's generic
                // projectile prefab would stretch a sphere the length of the shot.
                catalog.TryGetProjectilePrefab(projectileTemplateId, out prefab);
            }

            prefabsByProjectileTemplate[projectileTemplateId] = prefab;
            return prefab;
        }

        /// <summary>
        /// A line with no authoring behind it, so a session draws tracers before
        /// anyone binds art to the shot's projectile template. Built to the same
        /// convention an authored prefab follows: a cylinder is two units tall, so
        /// the body's local +Z becomes the shot's axis once it is turned onto it.
        /// </summary>
        private GameObject CreateProceduralTracer()
        {
            var root = new GameObject("Tracer");
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            body.name = "TracerBody";

            Collider collider = body.GetComponent<Collider>();
            if (collider != null)
            {
                DestroyNow(collider);
            }

            Renderer renderer = body.GetComponent<Renderer>();
            if (renderer != null)
            {
                // sharedMaterial with one instance of our own, rather than
                // Renderer.material: that property instantiates per renderer, and
                // outside play mode it leaks the copy into the scene and logs an
                // error for doing so.
                renderer.sharedMaterial = EnsureFallbackMaterial(renderer.sharedMaterial);
            }

            body.transform.SetParent(root.transform, false);
            body.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            body.transform.localScale = new Vector3(fallbackGirth, 1f, fallbackGirth);
            return root;
        }

        private Material EnsureFallbackMaterial(Material source)
        {
            if (fallbackMaterial == null && source != null)
            {
                fallbackMaterial = new Material(source)
                {
                    name = "TracerFallback",
                    color = fallbackColor,
                };
            }

            return fallbackMaterial != null ? fallbackMaterial : source;
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
