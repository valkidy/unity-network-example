using System;
using System.Collections.Generic;
using NetworkExample.Kernel;
using NetworkExample.Kernel.Host;
using NetworkExample.Kernel.Presentation;
using NetworkExample.UnityDemo.Common;
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
        private string pathScript = "+X";

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

        private bool started;
        private uint subjectNetId;
        private uint inputSeq = ScriptedInputSeqBase;
        private Vector3 subjectPosition;
        private double tickAccumulator;
        private uint renderStateCount;

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
            Debug.Log("LocomotionTest path: " + path.Description);

            presentationWorld.FallbackFactory = CreatePresentationInstance;
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

            Vector2 move = path.MoveInput(subjectPosition);
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

        private void UpdateObserverCamera()
        {
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
