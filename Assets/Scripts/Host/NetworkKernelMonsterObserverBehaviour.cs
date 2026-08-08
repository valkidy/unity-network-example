using System;
using System.Collections.Generic;
using NetworkExample.Kernel;
using NetworkExample.Kernel.Host;
using NetworkExample.Kernel.Presentation;
using NetworkExample.UnityDemo.CameraSystem;
using NetworkExample.UnityDemo.Common;
using NetworkExample.UnityDemo.Input;
using NetworkExample.UnityDemo.Rendering;
using UnityEngine;

namespace NetworkExample.UnityDemo.Host
{
    /// <summary>
    /// Drives the LocomotionTest scene: a listen host loaded with the
    /// <c>monster_observer</c> gameplay catalog, one scripted capture subject, and
    /// a kernel-posed skeleton rendered through
    /// <see cref="KernelEntityPresentationWorld"/>.
    ///
    /// The kernel call order mirrors
    /// <c>engine/src/tests/kernel_tests/locomotion_capture_driver.cc</c> tick for
    /// tick, so a run here is comparable with
    /// <c>capture/locomotion_tests/report.txt</c>:
    ///
    ///   Kernel_Create -> GameServer_CreateWithGameplayCatalogFromMemory
    ///   -> Kernel_SetGameplayCatalogSyncBundle -> Kernel_StartListenServer
    ///   per tick: Update / PollEvents -> HandleEvent / Tick
    ///             ServerCreateEntity (once) -> ServerGetEntityState
    ///             -> ServerSubmitEntityInput
    ///             -> GetRenderStates -> GetSkeletonRenderStates
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NetworkKernelMonsterObserverBehaviour : MonoBehaviour
    {
        /// <summary>
        /// Beats the game server's sentry patrol input for the same entity. The
        /// kernel resolves competing inputs by highest input_seq, and the sentry
        /// AI counts up from 0 (see the capture driver for the same constant).
        /// </summary>
        private const uint ScriptedInputSeqBase = 1000000u;

        /// <summary>
        /// The default scripted run: walk +X to x=30, back to x=-30, then home to
        /// the spawn, which makes the path a closed circuit that
        /// <see cref="loopPath"/> can replay indefinitely.
        ///
        /// 30 m is chosen against the terrain under the scene,
        /// Resources/Terrains/undulating_centered, which spans about +/-49 m on
        /// both axes. The subject walks at 2.5 m/s and yaws at 45 deg/s
        /// (monster_sim_actor.yaml), so a reversal is a half circle of radius
        /// 2.5 / (45 deg/s) = 3.2 m: it overshoots each turn point by about that
        /// much along the travel axis and swaps between two lanes 6.4 m apart in
        /// Z. Turning at 30 m therefore keeps the whole circuit inside roughly
        /// +/-34 m, well short of the terrain edge -- and the
        /// <see cref="patrolHalfExtents"/> guard catches whatever drift the
        /// turns leave behind over a long run.
        /// </summary>
        private const string PatrolPathScript =
            "+X; forward 30m; -X; forward 60m; +X; forward 30m";

        [Header("Host")]
        [SerializeField]
        private ushort port = 7777;

        [Tooltip(
            "Optional catalog bundle. When empty the bundle shipped inside the " +
            "kernel package is used.")]
        [SerializeField]
        private TextAsset gameplayCatalogBundle;

        [SerializeField]
        private string gameplayCatalogEntryPath = "monster_observer_gameplay_catalog.yaml";

        [SerializeField]
        private string contentNamespace = "monster_observer";

        [Header("Presentation")]
        [SerializeField]
        private KernelEntityPresentationWorld presentationWorld;

        [SerializeField]
        private Camera observerCamera;

        [SerializeField]
        private Vector3 cameraOffset = new Vector3(18f, 14f, -24f);

        [SerializeField]
        private bool createReferenceGround;

        [Tooltip(
            "Material for runtime-generated proxies. Leave empty to fall back to " +
            "Shader.Find, which is not build-safe.")]
        [SerializeField]
        private Material fallbackMaterial;

        [SerializeField]
        private float markerDiameter = NetworkMonsterSimRigFactory.DefaultMarkerDiameter;

        [SerializeField]
        private float linkThickness = NetworkMonsterSimRigFactory.DefaultLinkThickness;

        [Tooltip(
            "Drive the local player with WASD and orbit the camera with Q/E or the " +
            "arrow keys, as in Client.unity. Leave off for the scripted capture " +
            "run that the automated locomotion checks compare against the native " +
            "capture.")]
        [SerializeField]
        private bool manualControl;

        [Header("Capture subject")]
        [SerializeField]
        private uint entityTemplateId = 20u;

        [SerializeField]
        private Vector3 spawnPosition = new Vector3(0f, 10f, 0f);

        [Tooltip(
            "Same grammar as the native capture harness --path argument: an axis " +
            "(+X, -X, +Z, -Z), 'forward <n>m', 'turn <n>' and 'wait <n>s', " +
            "separated by ';'.")]
        [SerializeField]
        private string pathScript = PatrolPathScript;

        [Tooltip(
            "Replay the path from its first step once it finishes, so a closed " +
            "patrol circuit walks forever. Leave off for a one-shot run that " +
            "matches the native capture harness, which stops at the last step.")]
        [SerializeField]
        private bool loopPath = true;

        [Tooltip(
            "Half-extents of the walkable area in the XZ plane, centred on the " +
            "world origin like Resources/Terrains/undulating_centered (which is " +
            "about 49 m x 49 m per side). The scripted move input is mirrored " +
            "back inward outside this box, so an overshooting path cannot walk " +
            "the subject off the terrain. Zero disables the guard.")]
        [SerializeField]
        private Vector2 patrolHalfExtents = new Vector2(35f, 35f);

        [SerializeField]
        private int tickRate = 30;

        [Tooltip(
            "Samples to summarize in the console, matching --sampling in the " +
            "native capture. Zero disables the summary.")]
        [SerializeField]
        private int summarySampleCount = 300;

        private NetworkHost host;
        private KernelEvent[] events;
        private RenderEntityState[] renderStates;
        private SkeletonRenderStateBuffer skeletonStates;
        private readonly List<KernelSkeletonPoseApplicator> presentationApplicators =
            new List<KernelSkeletonPoseApplicator>();
        private readonly HashSet<int> seededApplicators = new HashSet<int>();
        private NetworkLocomotionPathScript path;
        private Material rigMaterial;
        private NetworkInputSampler inputSampler;
        private ThirdPersonFollowCamera followCamera;

        private bool started;
        private uint subjectNetId;
        private uint inputSeq = ScriptedInputSeqBase;
        private Vector3 subjectPosition;
        private double tickAccumulator;
        private uint renderStateCount;

        private bool patrolBoundsWarned;

        private int recordedSamples;
        private bool hasFirstSample;
        private Vector3 firstSamplePosition;
        private Vector3 lastSamplePosition;
        private double pathLength;
        private uint lastBoneCount;
        private bool summaryLogged;

        public uint SubjectNetId => subjectNetId;
        public int RecordedSamples => recordedSamples;

        private void Awake()
        {
            events = new KernelEvent[256];
            renderStates = new RenderEntityState[256];
            skeletonStates = new SkeletonRenderStateBuffer(4, 128);
        }

        private void OnEnable()
        {
            try
            {
                if (!TryStart())
                {
                    host?.Dispose();
                    host = null;
                    started = false;
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                host?.Dispose();
                host = null;
                started = false;
            }
        }

        private bool TryStart()
        {
            if (tickRate <= 0)
            {
                Debug.LogError("LocomotionTest tick rate must be positive.", this);
                return false;
            }
            if (presentationWorld == null)
            {
                presentationWorld = GetComponent<KernelEntityPresentationWorld>();
            }
            if (presentationWorld == null)
            {
                Debug.LogError(
                    "LocomotionTest requires a KernelEntityPresentationWorld.",
                    this);
                return false;
            }
            if (!NetworkLocomotionPathScript.TryParse(
                    pathScript,
                    tickRate,
                    out path,
                    out string pathError))
            {
                Debug.LogError("LocomotionTest path '" + pathScript + "': " + pathError, this);
                return false;
            }
            if (!TryResolveBundle(out byte[] bundleBytes))
            {
                return false;
            }

            // Read the bone layouts out of the same bytes the host is about to
            // load, so the rig this scene draws and the skeleton the kernel poses
            // are always the same version.
            if (!NetworkSkeletonManifests.TryLoad(bundleBytes, out string manifestError))
            {
                Debug.LogError(
                    "LocomotionTest could not read skeleton manifests: " + manifestError,
                    this);
                return false;
            }

            KernelConfig config = KernelConfig.CreateDefault(KernelMode.ListenServer);
            config.tick.server_tick_rate = (uint)tickRate;
            config.tick.snapshot_rate = (uint)Mathf.Max(1, tickRate / 2);
            config.max_render_states = (uint)renderStates.Length;
            config.max_events = (uint)events.Length;

            host = new NetworkHost();
            started = host.Start(
                port,
                config,
                new GameplayCatalogServerOptions
                {
                    BundleBytes = bundleBytes,
                    EntryPath = gameplayCatalogEntryPath,
                    ContentNamespace = contentNamespace,
                },
                out KernelGameplayCatalogLoadResult loadResult);
            if (!started)
            {
                Debug.LogError(
                    "LocomotionTest failed to start the listen host on port " + port +
                    " with entry '" + gameplayCatalogEntryPath + "'. " +
                    NetworkGameplayCatalogBundle.FormatLoadResult(loadResult),
                    this);
                return false;
            }

            Debug.Log(
                "LocomotionTest catalog loaded: " +
                NetworkGameplayCatalogBundle.FormatLoadResult(loadResult) +
                " entry=" + gameplayCatalogEntryPath);
            Debug.Log(
                "LocomotionTest path: " + path.Description +
                (loopPath ? " (looping)" : string.Empty));
            if (loopPath && path.IsUnbounded)
            {
                Debug.LogWarning(
                    "LocomotionTest path '" + pathScript + "' walks forever, so it " +
                    "never finishes and never loops. The subject will be turned " +
                    "back at the patrol bounds instead of patrolling.",
                    this);
            }

            presentationWorld.FallbackFactory = CreatePresentationInstance;
            if (manualControl)
            {
                EnableManualControl();
            }
            if (createReferenceGround)
            {
                CreateReferenceGround();
            }
            return true;
        }

        private bool TryResolveBundle(out byte[] bundleBytes)
        {
            if (gameplayCatalogBundle != null &&
                gameplayCatalogBundle.bytes != null &&
                gameplayCatalogBundle.bytes.Length > 0)
            {
                bundleBytes = gameplayCatalogBundle.bytes;
                return true;
            }

            if (!NetworkGameplayCatalogBundle.TryLoadDefault(out bundleBytes, out _))
            {
                return false;
            }
            if (bundleBytes == null || bundleBytes.Length == 0)
            {
                Debug.LogError("LocomotionTest gameplay catalog bundle is empty.", this);
                bundleBytes = null;
                return false;
            }
            return true;
        }

        private void Update()
        {
            if (!started || host == null)
            {
                return;
            }

            if (manualControl && host.IsLocalClientReady && inputSampler != null)
            {
                host.TrySubmitLocalInput(inputSampler.Sample());
            }

            float fixedDelta = 1f / tickRate;
            tickAccumulator += Time.unscaledDeltaTime;
            // Cap catch-up so a long editor stall cannot spin the kernel forever.
            int maxTicksThisFrame = Mathf.Max(1, tickRate);
            int ticks = 0;
            while (tickAccumulator >= fixedDelta && ticks < maxTicksThisFrame)
            {
                tickAccumulator -= fixedDelta;
                ++ticks;
                TickKernel(fixedDelta);
            }

            // Present in two passes against the raw, tick-aligned states the capture
            // driver also uses.
            //
            // A prefab rig with PreservePrefabBindPose enabled needs the native bind
            // pose to turn the kernel pose into a delta on top of the FBX bind pose,
            // and KernelEntityPresentationWorld only supplies a Kernel to the
            // applicator on its GetRenderStatesAtTime overload -- which renders on an
            // interpolation clock instead. So the first pass spawns and moves the
            // instances with no skeleton buffer, the bind pose is seeded into any new
            // applicator, and the second pass stages the pose.
            presentationWorld.Present(renderStates, renderStateCount, null);
            SeedNativeBindPoses();
            presentationWorld.Present(
                renderStates,
                renderStateCount,
                skeletonStates);
            UpdateObserverCamera();
        }

        /// <summary>
        /// Copies Kernel_GetSkeletonBindPose into every newly spawned rig whose
        /// binding applies the kernel pose as a delta on the prefab bind pose.
        /// Rigs built with PreservePrefabBindPose disabled need nothing here.
        /// </summary>
        private void SeedNativeBindPoses()
        {
            presentationWorld.GetComponentsInChildren(true, presentationApplicators);
            for (int index = 0; index < presentationApplicators.Count; ++index)
            {
                KernelSkeletonPoseApplicator applicator = presentationApplicators[index];
                if (!seededApplicators.Add(applicator.GetInstanceID()))
                {
                    continue;
                }

                var binding = applicator.GetComponent<KernelSkeletonBinding>();
                if (binding == null ||
                    !binding.PreservePrefabBindPose ||
                    binding.Bones == null ||
                    binding.Bones.Length == 0)
                {
                    continue;
                }

                var bindPose = new KernelBoneLocalTransform[binding.Bones.Length];
                uint returned = host.Kernel.GetSkeletonBindPose(
                    binding.SkeletonAssetId,
                    binding.SkeletonContentHash,
                    bindPose);
                if (returned != (uint)bindPose.Length)
                {
                    Debug.LogError(
                        "LocomotionTest: skeleton asset " + binding.SkeletonAssetId +
                        " returned " + returned + " bind-pose bones, expected " +
                        bindPose.Length + ".",
                        applicator);
                    continue;
                }
                if (!applicator.TrySetNativeBindPose(bindPose, out string bindError))
                {
                    Debug.LogError(
                        "LocomotionTest: native bind pose rejected: " + bindError,
                        applicator);
                }
            }
        }

        private void TickKernel(float fixedDelta)
        {
            host.Update(fixedDelta, events);

            if (subjectNetId == 0 && !TrySpawnSubject())
            {
                return;
            }

            if (host.Kernel.ServerGetEntityState(
                    subjectNetId,
                    out KernelServerEntityState subjectState) &&
                subjectState.valid != 0u)
            {
                subjectPosition = new Vector3(
                    subjectState.position.x,
                    subjectState.position.y,
                    subjectState.position.z);
            }

            KeepSubjectRelevant();

            if (loopPath && path.Finished)
            {
                path.Restart();
            }

            Vector2 move = ClampToPatrolArea(path.MoveInput(subjectPosition));
            host.Kernel.ServerSubmitEntityInput(
                subjectNetId,
                new KernelPlayerInput
                {
                    input_seq = inputSeq++,
                    move = new KernelVec2(move.x, move.y),
                });

            renderStateCount = host.GetRenderStates(renderStates);
            host.GetSkeletonRenderStates(skeletonStates);
            RecordSample();
        }

        /// <summary>
        /// Pins the local player to the capture subject so the subject stays inside
        /// the kernel's relevance radius.
        ///
        /// The kernel culls entities more than
        /// kDefaultEntityRelevanceDistanceMeters (40 m, a hard-coded constant in
        /// engine/src/kernel/src/kernel.cc with no public config knob) from the
        /// local player, and the listen-server's own client runs the same filter as
        /// a remote peer. Left unpinned, the player stays at the origin and the
        /// subject crosses 40 m from it at about x=39.7: the kernel sends
        /// KernelDespawnReason_OutOfRange, the entity leaves Kernel_GetRenderStates
        /// and KernelEntityPresentationWorld destroys the instance -- the subject
        /// vanishes while still alive and walking on the server. The patrol turns
        /// around at 30 m, inside that radius, but a longer leg or a re-centred
        /// patrol box would hit it again, so the pin stays.
        ///
        /// Under manual control the player is driven by input instead, so following
        /// the subject is the operator's job.
        /// </summary>
        private void KeepSubjectRelevant()
        {
            if (manualControl || host.LocalPlayerNetId == 0)
            {
                return;
            }

            host.Kernel.ServerSetEntityTransform(
                host.LocalPlayerNetId,
                new KernelVec3(
                    subjectPosition.x,
                    subjectPosition.y,
                    subjectPosition.z),
                new KernelQuat(0f, 0f, 0f, 1f));
        }

        /// <summary>
        /// Mirrors any move component that points further out of the patrol box,
        /// so the subject cannot be walked past the terrain edge and dropped into
        /// the void.
        ///
        /// This is a backstop, not the steering: the path script is authored to
        /// turn around well inside the box (see <see cref="PatrolPathScript"/>).
        /// It exists because a turn is a yaw-rate-limited arc, so a scripted
        /// reversal always overshoots its turn point, and because Forward steps
        /// measure distance from wherever the previous step happened to end --
        /// both leave a little residue per lap that a looping path accumulates.
        /// Mirroring rather than zeroing keeps the input at full magnitude, which
        /// keeps the gait walking instead of stalling at the boundary.
        /// </summary>
        private Vector2 ClampToPatrolArea(Vector2 move)
        {
            bool clamped = false;
            if (patrolHalfExtents.x > 0f &&
                Mathf.Abs(subjectPosition.x) > patrolHalfExtents.x &&
                move.x * subjectPosition.x > 0f)
            {
                move.x = -move.x;
                clamped = true;
            }
            if (patrolHalfExtents.y > 0f &&
                Mathf.Abs(subjectPosition.z) > patrolHalfExtents.y &&
                move.y * subjectPosition.z > 0f)
            {
                move.y = -move.y;
                clamped = true;
            }

            if (clamped && !patrolBoundsWarned)
            {
                patrolBoundsWarned = true;
                Debug.LogWarning(
                    "LocomotionTest: the subject reached the patrol bounds (+/-" +
                    patrolHalfExtents.x.ToString("F1") + " m X, +/-" +
                    patrolHalfExtents.y.ToString("F1") +
                    " m Z) at " + subjectPosition.ToString("F2") +
                    " and was turned back inward. The path '" + pathScript +
                    "' is walking outside the box it was authored for.",
                    this);
            }
            return move;
        }

        private bool TrySpawnSubject()
        {
            var createInfo = new KernelServerEntityCreateInfo
            {
                owner_peer = 0u,
                position = new KernelVec3(
                    spawnPosition.x,
                    spawnPosition.y,
                    spawnPosition.z),
                rotation = new KernelQuat(0f, 0f, 0f, 1f),
                entity_template_id = entityTemplateId,
            };
            if (!host.Kernel.ServerCreateEntity(createInfo, out uint netId) || netId == 0)
            {
                Debug.LogError(
                    "LocomotionTest failed to create entity template " +
                    entityTemplateId + ".",
                    this);
                started = false;
                return false;
            }

            subjectNetId = netId;
            subjectPosition = spawnPosition;
            Debug.Log(
                "LocomotionTest subject net_id=" + subjectNetId + " spawned at " +
                spawnPosition.ToString("F3"));
            return true;
        }

        // Mirrors the capture driver's sampling gate: a sample counts only once the
        // root render state and the matching full skeleton pose are both available.
        private void RecordSample()
        {
            if (summarySampleCount <= 0 || recordedSamples >= summarySampleCount)
            {
                return;
            }

            int stateCount = renderStateCount > (uint)renderStates.Length
                ? renderStates.Length
                : (int)renderStateCount;
            var subject = default(RenderEntityState);
            bool foundSubject = false;
            for (int index = 0; index < stateCount; ++index)
            {
                if (renderStates[index].net_id == subjectNetId)
                {
                    subject = renderStates[index];
                    foundSubject = true;
                    break;
                }
            }
            if (!foundSubject ||
                skeletonStates.Result.status != KernelConstants.SkeletonRenderStatusSuccess)
            {
                return;
            }

            int skeletonCount = skeletonStates.StateCount > (uint)skeletonStates.States.Length
                ? skeletonStates.States.Length
                : (int)skeletonStates.StateCount;
            uint boneCount = 0;
            for (int index = 0; index < skeletonCount; ++index)
            {
                if (skeletonStates.States[index].entity_net_id == subjectNetId)
                {
                    boneCount = skeletonStates.States[index].bone_count;
                    break;
                }
            }
            if (boneCount == 0)
            {
                return;
            }

            var position = new Vector3(
                subject.position.x,
                subject.position.y,
                subject.position.z);
            if (!hasFirstSample)
            {
                hasFirstSample = true;
                firstSamplePosition = position;
            }
            else
            {
                // Planar, to match the native capture analyzer's path length.
                pathLength += new Vector2(
                    position.x - lastSamplePosition.x,
                    position.z - lastSamplePosition.z).magnitude;
            }
            lastSamplePosition = position;
            lastBoneCount = boneCount;
            ++recordedSamples;

            if (recordedSamples >= summarySampleCount && !summaryLogged)
            {
                summaryLogged = true;
                LogSummary();
            }
        }

        private void LogSummary()
        {
            Vector3 displacement = lastSamplePosition - firstSamplePosition;
            float planar = new Vector2(displacement.x, displacement.z).magnitude;
            Debug.Log(
                "LocomotionTest summary (" + recordedSamples + " samples @ " +
                tickRate + " Hz = " +
                ((float)recordedSamples / tickRate).ToString("F2") + " s)\n" +
                "  path:         " + path.Description + "\n" +
                "  start:        " + firstSamplePosition.ToString("F3") + "\n" +
                "  end:          " + lastSamplePosition.ToString("F3") + "\n" +
                "  displacement: dX " + displacement.x.ToString("F3") +
                " m  dZ " + displacement.z.ToString("F3") +
                " m  planar " + planar.ToString("F3") + " m\n" +
                "  path length:  " + pathLength.ToString("F3") + " m\n" +
                "  bone count:   " + lastBoneCount);
        }

        private GameObject CreatePresentationInstance(RenderEntityState state)
        {
            if (state.entity_type == KernelEntityType.Actor &&
                state.template_id == entityTemplateId)
            {
                return NetworkMonsterSimRigFactory.Create(
                    "MonsterSimRig",
                    markerDiameter,
                    linkThickness,
                    EnsureRigMaterial());
            }

            // In scripted mode the local player is only a relevance anchor pinned
            // to the subject (see KeepSubjectRelevant), so drawing it would put a
            // capsule inside the monster.
            if (!manualControl &&
                state.net_id != 0 &&
                state.net_id == host.LocalPlayerNetId)
            {
                return null;
            }

            GameObject proxy = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Collider collider = proxy.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }
            // CreatePrimitive hands out the built-in Default-Material, which runs the
            // Standard shader and draws magenta under URP.
            Material material = EnsureRigMaterial();
            if (material != null)
            {
                proxy.GetComponent<MeshRenderer>().sharedMaterial = material;
            }
            return proxy;
        }

        private Material EnsureRigMaterial()
        {
            if (fallbackMaterial != null)
            {
                return fallbackMaterial;
            }
            if (rigMaterial != null)
            {
                return rigMaterial;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }
            if (shader == null)
            {
                return null;
            }

            rigMaterial = new Material(shader)
            {
                name = "MonsterSimRig (runtime)",
                hideFlags = HideFlags.HideAndDontSave,
            };
            rigMaterial.color = new Color(0.19f, 0.62f, 0.75f);
            return rigMaterial;
        }

        private void CreateReferenceGround()
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Reference Ground";
            ground.transform.SetParent(transform, false);
            ground.transform.localScale = new Vector3(20f, 1f, 20f);
            Collider collider = ground.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }
        }

        /// <summary>
        /// Manual control mirrors Client.unity: WASD drives the local player,
        /// Q/E and the arrow keys orbit a ThirdPersonFollowCamera, and movement is
        /// camera-relative. The default tuning suits the human-sized player
        /// template, which also gives a sense of scale next to the ~30 m monster.
        /// </summary>
        private void EnableManualControl()
        {
            followCamera = NetworkDemoScene.EnsureDefaultView();

            inputSampler = GetComponent<NetworkInputSampler>();
            if (inputSampler == null)
            {
                inputSampler = gameObject.AddComponent<NetworkInputSampler>();
            }
            inputSampler.SetViewTransform(followCamera.transform);

            // The fixed observer camera would fight the follow camera for the
            // same transform.
            if (observerCamera != null &&
                observerCamera.transform == followCamera.transform)
            {
                observerCamera = null;
            }
        }

        private void UpdateObserverCamera()
        {
            if (manualControl)
            {
                UpdateFollowCameraTarget();
                return;
            }
            if (observerCamera == null || subjectNetId == 0)
            {
                return;
            }
            if (!presentationWorld.TryGetPresentationTransform(
                    entityTemplateId,
                    out Transform target))
            {
                return;
            }

            observerCamera.transform.position = target.position + cameraOffset;
            observerCamera.transform.LookAt(target.position);
        }

        private void UpdateFollowCameraTarget()
        {
            if (followCamera == null)
            {
                return;
            }
            uint playerNetId = host.LocalPlayerNetId;
            if (playerNetId == 0)
            {
                return;
            }

            // KernelEntityPresentationWorld can only be queried by template id, so
            // resolve the local player's template from the render states first.
            int count = renderStateCount > (uint)renderStates.Length
                ? renderStates.Length
                : (int)renderStateCount;
            for (int index = 0; index < count; ++index)
            {
                if (renderStates[index].net_id != playerNetId)
                {
                    continue;
                }
                if (presentationWorld.TryGetPresentationTransform(
                        renderStates[index].template_id,
                        out Transform player))
                {
                    followCamera.SetTarget(player);
                }
                return;
            }
        }

        private void OnDisable()
        {
            if (presentationWorld != null)
            {
                presentationWorld.FallbackFactory = null;
                presentationWorld.DespawnAll();
            }
            host?.Dispose();
            host = null;
            started = false;
            subjectNetId = 0;
            inputSeq = ScriptedInputSeqBase;
            tickAccumulator = 0.0;
            renderStateCount = 0;
            seededApplicators.Clear();
            patrolBoundsWarned = false;
            recordedSamples = 0;
            hasFirstSample = false;
            pathLength = 0.0;
            summaryLogged = false;
            path = null;
            if (rigMaterial != null)
            {
                Destroy(rigMaterial);
                rigMaterial = null;
            }
        }
    }
}
