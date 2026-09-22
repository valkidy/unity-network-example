using System;
using System.Collections.Generic;
using System.IO;
using NetworkExample.Kernel;
using NetworkExample.Kernel.Client;
using NetworkExample.UnityDemo.CameraSystem;
using NetworkExample.UnityDemo.Common;
using NetworkExample.UnityDemo.Input;
using NetworkExample.UnityDemo.LocalAgent;
using NetworkExample.UnityDemo.UI;
using NetworkExample.UnityDemo.Items;
using NetworkExample.UnityDemo.Rendering;
using UnityEngine;

namespace NetworkExample.UnityDemo.Client
{
    [DisallowMultipleComponent]
    public sealed class ClientRunner : MonoBehaviour
    {
        /// <summary>
        /// How long to wait before configuring the synchronized catalog again
        /// after a failed attempt. Short enough that a bundle still being written
        /// to disk is picked up within a frame or two of landing, long enough
        /// that a permanent failure does not re-read and re-hash it every frame.
        /// </summary>
        private const float CatalogConfigureRetrySeconds = 0.25f;

        [SerializeField]
        private string serverAddress = "127.0.0.1:7777";

        [SerializeField]
        private int maxEvents = 256;

        [SerializeField]
        private int maxRenderStates = 256;

        [SerializeField]
        private int maxActionEvents = 128;

        [SerializeField]
        private bool enableDiagnostics = true;

        [Tooltip(
            "Logs every local action the kernel did not accept, with the reason " +
            "it gave. Off by default because a busy fight can produce one per " +
            "commit; turn it on to find out why a burst stopped.")]
        [SerializeField]
        private bool logActionResultFailures = true;

        [Tooltip(
            "Reports when the fire trigger is held but nothing is coming out of " +
            "it, with the authoritative action state beside what the sampler " +
            "believes. Rare by nature, so it is on by default.")]
        [SerializeField]
        private bool logFireStalls = true;

        [Tooltip(
            "How long the trigger may be held with no commit progress before that " +
            "counts as a stall. Must clear the slowest weapon's cadence.")]
        [SerializeField]
        private float fireStallSeconds = 1.5f;

        [SerializeField]
        private float diagnosticLogIntervalSeconds = 1.0f;

        [SerializeField]
        private float readyWithoutRenderWarningSeconds = 1.0f;

        [SerializeField]
        private bool enableVisualDebug = true;

        [SerializeField]
        [Min(1f)]
        [Tooltip(
            "Temporary prediction workaround: limits input submission to the " +
            "server simulation rate so high render rates do not oversimulate prediction.")]
        private float inputSubmissionRateHz = 30f;

        [SerializeField]
        [Tooltip(
            "Logs how much of each leg's bone length the hip-to-foot span uses, " +
            "once a second. A two-bone chain at 100% is straight: it has no bend " +
            "plane left, so the pole vector alone decides where the limb points " +
            "and the foot can no longer reach its target.")]
        private bool logLegReach = true;

        [SerializeField]
        [Tooltip(
            "Logs where each rendered skeleton's pose came from. Its call site is " +
            "commented out; re-enable it there if pose provenance is in question again.")]
        private bool logSkeletonPoseProvenance = true;

        [Header("Local Agent")]
        [SerializeField]
        private bool enableLocalAgent;

        [SerializeField]
        private LocalAgentSettings localAgentSettings = new LocalAgentSettings();

        private readonly LocalAgentController localAgent = new LocalAgentController();
        private readonly LocalAgentPerception localAgentPerception = new LocalAgentPerception();
        // Every collider of the observed frame: props and actors can hide what is behind
        // them only through these, as the terrain is the scene's only Unity collider.
        private readonly KernelColliderShapeSource agentColliderShapes =
            new KernelColliderShapeSource(nameof(ClientRunner) + " agent sight");
        private readonly ShapeLineOfSight agentLineOfSight = new ShapeLineOfSight();
        private bool agentMode;
        private bool pendingInputHandoff;
        private int manualWeaponSlot = -1;
        private RenderEntityState[] agentObservations;
        private int agentObservationCount;
        private float agentObservationTime;
        // Damage events to the local player since the agent last perceived; events
        // arrive every frame, the agent perceives once per input tick.
        private int agentDamageTaken;
        private Dictionary<byte, KernelActionTriggerMode> weaponFireTriggerModes;
        private DetourNavMeshQuery agentNavMesh;
        /// <summary>The synchronized catalog's navigation mesh, or null when it could not be read.</summary>
        public DetourNavMeshQuery AgentNavMesh => agentNavMesh;
        public LocalAgentState AgentState => localAgent.State;
        public uint AgentFollowTargetId => localAgent.FollowTargetId;
        public uint AgentAttackTargetId => localAgent.AttackTargetId;
        public bool EnableLocalAgent { get => enableLocalAgent; set => enableLocalAgent = value; }

        private NetworkClient client;
        private KernelEvent[] events;
        private RenderEntityState[] renderStates;
        private NetworkFireStallDiagnostic fireStallDiagnostic;
        private SkeletonRenderStateBuffer skeletonPoseStates;
        private float nextSkeletonPoseLogTime;
        private float nextLegReachLogTime;
        private KernelEntityLifecycleEvent[] lifecycleEvents;
        private KernelLocalActionResult[] localActionResults;
        private KernelRemoteActionPresentationEvent[] remoteActionEvents;
        private NetworkInputSampler inputSampler;
        private NetworkItemPropInputSampler itemPropInputSampler;
        private NetworkItemPropController itemPropController;
        private NetworkEntityRegistry entityRegistry;
        private NetworkRenderStateApplier renderStateApplier;
        private NetworkDebugView debugView;
        private NetworkHitscanTracers hitscanTracers;
        private NetworkHitSplatters hitSplatters;
        private NetworkPropShatter propShatter;
        private ThirdPersonFollowCamera followCamera;
        private AimReticleView aimReticleView;
        private readonly NetworkPresentationClock presentationClock = new NetworkPresentationClock();
        private NetworkInputSubmissionClock inputSubmissionClock;
        private GameplayCatalogSyncOptions gameplayCatalogSyncOptions;
        private bool started;
        private bool readinessLogged;
        private float nextDiagnosticLogTime;
        private float readyWithoutRenderSeconds;
        private bool readyWithoutRenderWarningLogged;
        private NetworkClientConnectionState lastConnectionState;
        // The synchronized catalog is what gives the sampler its weapon loadout,
        // and Update submits no input at all without one. Tracked separately from
        // the connection state so a failed attempt can be retried while the
        // connection stays ready.
        private bool synchronizedCatalogConfigured;
        private bool catalogConfigureFailureLogged;
        private float nextCatalogConfigureTime;

        private void Awake()
        {
            EnsureComponents();
            inputSubmissionClock = new NetworkInputSubmissionClock(
                Mathf.Max(1f, inputSubmissionRateHz));
            events = new KernelEvent[Mathf.Max(1, maxEvents)];
            renderStates = new RenderEntityState[Mathf.Max(1, maxRenderStates)];
            agentObservations = new RenderEntityState[renderStates.Length];
            fireStallDiagnostic = new NetworkFireStallDiagnostic(fireStallSeconds);
            lifecycleEvents = new KernelEntityLifecycleEvent[Mathf.Max(1, maxEvents)];
            localActionResults = new KernelLocalActionResult[Mathf.Max(1, maxActionEvents)];
            remoteActionEvents =
                new KernelRemoteActionPresentationEvent[Mathf.Max(1, maxActionEvents)];
        }

        private void OnEnable()
        {
            try
            {
                NetworkKernelVersionLogger.Log();
                client = new NetworkClient();
                ConfigurePhysicsBeforeStart(client.Kernel);
                gameplayCatalogSyncOptions = CreateGameplayCatalogSyncOptions();
                GameplayCatalogSyncOptions syncOptions = gameplayCatalogSyncOptions;
                started = client.Start(serverAddress, syncOptions);
                if (!started)
                {
                    Debug.LogError(
                        "Client failed to start gameplay catalog sync for " +
                        serverAddress +
                        ": " +
                        FormatCatalogSyncResult(client.CatalogSyncResult));
                    client.Dispose();
                    client = null;
                    return;
                }

                lastConnectionState = client.ConnectionState;
                Debug.Log(
                    "Client syncing gameplay catalog and connecting to " +
                    serverAddress);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                client?.Dispose();
                client = null;
                started = false;
            }
        }

        private void Update()
        {
            if (!started || client == null)
            {
                return;
            }

            UpdateInputMode();
            // Human aim is polled at render rate. Agent aim comes only from its command.
            if (!agentMode && !pendingInputHandoff)
            {
                inputSampler.UpdateAimState();
                if (followCamera != null)
                    inputSampler.SetAimDirection(followCamera.AimDirection);
            }
            if (followCamera != null)
                followCamera.SetAiming(inputSampler.IsAiming);

            KernelActionIntent predictedIntent = default;
            if (client.IsReady &&
                inputSampler.HasWeaponLoadout &&
                inputSubmissionClock.ShouldSubmit(Time.unscaledDeltaTime))
            {
                KernelPlayerInput input = SampleCurrentInput();
                if (client.TrySubmitInput(input))
                {
                    pendingInputHandoff = false;
                    predictedIntent = input.action_intent;
                    renderStateApplier.BeginPredictedLocalAction(
                        client.LocalPlayerNetId,
                        predictedIntent);
                }
                else
                {
                    inputSampler.StopActionInput(input.action_intent.action_instance_id);
                }
            }
            else if (!client.IsReady)
            {
                inputSubmissionClock.Reset();
                ClearAgentObservation();
            }

            uint eventCount = client.Update(Time.unscaledDeltaTime, events);
            WarnIfBufferFilled(eventCount, events.Length, "event");
            if (agentMode)
                agentDamageTaken += LocalAgentPerception.CountDamage(events,
                    SafeCount(eventCount, events.Length), client.LocalPlayerNetId);
            LogDiagnosticEvents(eventCount);
            LogConnectionState();
            itemPropController.UpdateAuthoritativeState(client);

            uint localActionResultCount = client.Kernel.PollLocalActionResults(
                localActionResults);
            uint remoteActionEventCount = client.Kernel.PollRemoteActionPresentationEvents(
                remoteActionEvents);
            uint lifecycleEventCount = client.Kernel.PollEntityLifecycleEvents(
                lifecycleEvents);
            WarnIfBufferFilled(
                localActionResultCount,
                localActionResults.Length,
                "local action result");
            WarnIfBufferFilled(
                remoteActionEventCount,
                remoteActionEvents.Length,
                "remote action presentation");
            WarnIfBufferFilled(
                lifecycleEventCount,
                lifecycleEvents.Length,
                "entity lifecycle");
            CompleteLocalActions(localActionResultCount);

            if (client.ConnectionState == NetworkClientConnectionState.Failed ||
                client.ConnectionState == NetworkClientConnectionState.Disconnected)
            {
                inputSampler.ResetSession();
                itemPropController.ResetSession();
                inputSubmissionClock.Reset();
                ClearAgentObservation();
                started = false;
                return;
            }

            ulong clientRenderTimeUs = presentationClock.Advance(
                Time.unscaledDeltaTime);
            uint renderCount = client.GetRenderStatesAtTime(clientRenderTimeUs, renderStates);
            // Sampled at the SAME instant as the roots above. The bone locals
            // describe the rig relative to the root of the moment they were
            // solved for, so composing them onto a root from another instant
            // translates every leg by the difference.
            if (skeletonPoseStates == null)
            {
                skeletonPoseStates = new SkeletonRenderStateBuffer(8, 512);
            }
            client.Kernel.GetSkeletonRenderStatesAtTime(
                clientRenderTimeUs,
                skeletonPoseStates);
            WarnIfBufferFilled(renderCount, renderStates.Length, "render state");
            int safeRenderCount = renderCount > (uint)renderStates.Length
                ? renderStates.Length
                : (int)renderCount;
            LogDiagnosticRenderSummary(renderCount, safeRenderCount);
            WarnIfReadyWithoutRenderStates(safeRenderCount);
            ObserveFireStall(safeRenderCount);
            renderStateApplier.Apply(renderStates, safeRenderCount);
            renderStateApplier.ApplySkeletonPoses(client.Kernel, skeletonPoseStates);
            UpdateCameraTarget(client.LocalPlayerNetId);
            renderStateApplier.ApplyKernelEvents(
                events,
                SafeCount(eventCount, events.Length));
            renderStateApplier.ApplyLocalActionResults(
                client.LocalPlayerNetId,
                localActionResults,
                SafeCount(localActionResultCount, localActionResults.Length));
            renderStateApplier.ApplyRemoteActionPresentationEvents(
                remoteActionEvents,
                SafeCount(remoteActionEventCount, remoteActionEvents.Length));
            renderStateApplier.ApplyEntityLifecycleEvents(
                lifecycleEvents,
                SafeCount(lifecycleEventCount, lifecycleEvents.Length));
            if (followCamera != null)
            {
                itemPropController.SetAimDirection(followCamera.AimDirection);
            }
            if (!agentMode && !pendingInputHandoff)
                itemPropController.ProcessInput(client, renderStates, safeRenderCount);

            // Retain this completed frame for the next input tick. Lifecycle events
            // win over render samples from the same update.
            Array.Copy(renderStates, agentObservations, safeRenderCount);
            agentObservationCount = safeRenderCount;
            agentObservationTime = Time.unscaledTime;
            for (int i = 0; i < SafeCount(lifecycleEventCount, lifecycleEvents.Length); i++)
                for (int j = 0; j < agentObservationCount; j++)
                    if (agentObservations[j].net_id == lifecycleEvents[i].net_id)
                        agentObservations[j].net_id = 0;
            if (agentMode && localAgentSettings != null &&
                localAgentSettings.perceptionMode == PerceptionMode.Limited)
                CaptureAgentSight();

            if (debugView != null)
            {
                debugView.Capture(client.Kernel, renderStates, safeRenderCount);
            }

            // Pose provenance is settled -- every rendered skeleton reports
            // PROCEDURAL with a server-tracked poseTick -- so its once-a-second
            // line is commented out rather than deleted, in case a later change
            // needs it again.
            // LogSkeletonPoseProvenance();
            LogLegReach();

            if (!readinessLogged && client.IsReady)
            {
                readinessLogged = true;
                Debug.Log(
                    "Client local player ready. peer=" +
                    client.LocalPeerId +
                    " player=" +
                    client.LocalPlayerNetId);
            }
        }

        private void OnDisable()
        {
            ClearAgentObservation();
            agentMode = false;
            pendingInputHandoff = false;
            aimReticleView?.SetVisible(true);
            followCamera?.SetTarget(null);
            renderStateApplier?.Clear();
            inputSampler?.ResetSession();
            itemPropController?.ResetSession();
            client?.Dispose();
            client = null;
            gameplayCatalogSyncOptions = null;
            started = false;
            readinessLogged = false;
            nextDiagnosticLogTime = 0f;
            readyWithoutRenderSeconds = 0f;
            readyWithoutRenderWarningLogged = false;
            lastConnectionState = NetworkClientConnectionState.Idle;
            synchronizedCatalogConfigured = false;
            catalogConfigureFailureLogged = false;
            nextCatalogConfigureTime = 0f;
            presentationClock.Reset();
            inputSubmissionClock?.Reset();
        }

        private void UpdateInputMode()
        {
            if (agentMode == enableLocalAgent) return;
            agentMode = enableLocalAgent;
            pendingInputHandoff = true;
            // Give the player back the weapon they had before the agent took over.
            if (agentMode)
                manualWeaponSlot = inputSampler.SelectedWeaponSlot;
            else if (manualWeaponSlot >= 0)
                inputSampler.TrySelectWeaponSlot(manualWeaponSlot);
            localAgent.Reset();
            localAgentPerception.Reset();
            agentDamageTaken = 0;
            aimReticleView?.SetVisible(!agentMode);
            if (agentMode && (localAgentSettings == null ||
                localAgentSettings.enemyTemplateIds == null ||
                localAgentSettings.enemyTemplateIds.Length == 0))
                Debug.LogWarning("Local Agent has no enemy template IDs; it will only follow players.");
        }

        private KernelPlayerInput SampleCurrentInput()
        {
            if (pendingInputHandoff)
                return inputSampler.SampleExplicit(Vector2.zero, Vector3.forward, false, false, false);
            if (!agentMode) return inputSampler.Sample();
            SelectAgentWeapon();
            bool hasWeapon = client.Kernel.TryGetLocalWeaponState(out KernelLocalWeaponState weapon);
            if (hasWeapon)
                weapon = ResolveAgentWeapon(weapon, inputSampler);
            // The server fires whatever the input selects, so that decides the trigger.
            bool holdTrigger = weaponFireTriggerModes != null &&
                weaponFireTriggerModes.TryGetValue(inputSampler.SelectedWeaponId,
                    out KernelActionTriggerMode triggerMode) &&
                triggerMode == KernelActionTriggerMode.Hold;
            localAgentPerception.Update(localAgentSettings, agentObservationTime,
                agentObservations, agentObservationCount, client.LocalPlayerNetId,
                localAgent.LastAimDirection, agentLineOfSight, agentDamageTaken);
            agentDamageTaken = 0;
            LocalAgentCommand command = localAgent.Step(localAgentSettings, localAgentPerception,
                hasWeapon, weapon, Time.unscaledTime - agentObservationTime, holdTrigger,
                inputSampler.HeldFireActionInstanceId != 0);
            return inputSampler.SampleExplicit(command.Move, command.AimDirection,
                command.Aim, command.Fire, command.Reload);
        }

        /// <summary>
        /// Fills in what a client's weapon state cannot say on its own.
        /// </summary>
        /// <remarks>
        /// A client's kernel has no weapon component on the local player, so it
        /// reports the snapshot's active slot but never sets WeaponIdValid; the
        /// loadout turns the slot into a weapon. The server moves its active slot
        /// only when an action commits, so right after the agent switches weapon
        /// the snapshot still describes the old one. Its ammo says nothing about
        /// the selected weapon then, and the agent fires rather than reloading
        /// the wrong magazine; that first shot is what makes the server switch.
        /// </remarks>
        private static KernelLocalWeaponState ResolveAgentWeapon(
            KernelLocalWeaponState weapon, NetworkInputSampler sampler)
        {
            if ((weapon.flags & KernelConstants.LocalWeaponStateFlagWeaponIdValid) == 0 &&
                sampler.TryGetWeaponIdForSlot(weapon.active_weapon_slot, out byte slotWeapon))
            {
                weapon.weapon_id = slotWeapon;
                weapon.flags |= KernelConstants.LocalWeaponStateFlagWeaponIdValid;
            }
            if ((weapon.flags & KernelConstants.LocalWeaponStateFlagWeaponIdValid) != 0 &&
                weapon.weapon_id != sampler.SelectedWeaponId)
            {
                weapon.weapon_id = sampler.SelectedWeaponId;
                weapon.flags &= unchecked((byte)~KernelConstants.LocalWeaponStateFlagReloading);
                weapon.ammo = weapon.authoritative_ammo = 1;
            }
            return weapon;
        }

        // Applied every agent tick: the loadout arrives with the catalog and a
        // session reset reselects the catalog's active slot.
        private void SelectAgentWeapon()
        {
            int preferredWeapon = localAgentSettings != null ? localAgentSettings.preferredWeaponId : -1;
            if (preferredWeapon >= 0 && preferredWeapon <= byte.MaxValue &&
                inputSampler.SelectedWeaponId != preferredWeapon)
                inputSampler.TrySelectWeaponId((byte)preferredWeapon);
        }

        private void ClearAgentObservation()
        {
            agentObservationCount = 0;
            agentDamageTaken = 0;
            localAgent.Reset();
            localAgentPerception.Reset();
            agentColliderShapes.Clear();
            agentLineOfSight.SetShapes(null, 0);
        }

        // Taken with the observation, so the colliders match the positions the agent sees.
        private void CaptureAgentSight()
        {
            agentColliderShapes.Capture(client.Kernel, agentObservations, agentObservationCount);
            agentLineOfSight.SetShapes(agentColliderShapes.Shapes, agentColliderShapes.Count);
            if (agentLineOfSight.TerrainBlocks == null)
                agentLineOfSight.TerrainBlocks = TerrainBlocksSight;
        }

        private bool TerrainBlocksSight(Vector3 from, Vector3 to) =>
            Physics.Linecast(from, to, localAgentSettings != null ? localAgentSettings.sightBlockingLayers : 1,
                QueryTriggerInteraction.Ignore);

        private void EnsureComponents()
        {
            followCamera = NetworkDemoScene.EnsureDefaultView();

            aimReticleView = GetComponent<AimReticleView>();
            if (aimReticleView == null)
            {
                aimReticleView = gameObject.AddComponent<AimReticleView>();
            }
            aimReticleView.Configure(followCamera);

            inputSampler = GetComponent<NetworkInputSampler>();
            if (inputSampler == null)
            {
                inputSampler = gameObject.AddComponent<NetworkInputSampler>();
            }
            inputSampler.SetViewTransform(followCamera.transform);

            itemPropInputSampler = GetComponent<NetworkItemPropInputSampler>();
            if (itemPropInputSampler == null)
            {
                itemPropInputSampler = gameObject.AddComponent<NetworkItemPropInputSampler>();
            }

            itemPropController = GetComponent<NetworkItemPropController>();
            if (itemPropController == null)
            {
                itemPropController = gameObject.AddComponent<NetworkItemPropController>();
            }
            itemPropController.Configure(
                itemPropInputSampler,
                followCamera.transform,
                // Reads the field at call time, so it still resolves once the
                // client exists and the local player has a net id.
                () => renderStateApplier?.TriggerLocalItemThrow(
                    client == null ? 0U : client.LocalPlayerNetId));

            entityRegistry = GetComponent<NetworkEntityRegistry>();
            if (entityRegistry == null)
            {
                entityRegistry = gameObject.AddComponent<NetworkEntityRegistry>();
            }

            NetworkPrefabRegistry prefabRegistry = GetComponent<NetworkPrefabRegistry>();
            if (prefabRegistry == null)
            {
                prefabRegistry = gameObject.AddComponent<NetworkPrefabRegistry>();
            }

            renderStateApplier = GetComponent<NetworkRenderStateApplier>();
            if (renderStateApplier == null)
            {
                renderStateApplier = gameObject.AddComponent<NetworkRenderStateApplier>();
            }

            debugView = GetComponent<NetworkDebugView>();
            if (debugView == null)
            {
                debugView = gameObject.AddComponent<NetworkDebugView>();
            }
            debugView.SetEnabled(enableVisualDebug);

            Debug.Log("ClientRunner configured with input sampler " + inputSampler.GetType().Name);
            hitscanTracers = GetComponent<NetworkHitscanTracers>();
            if (hitscanTracers == null)
            {
                hitscanTracers = gameObject.AddComponent<NetworkHitscanTracers>();
            }

            hitSplatters = GetComponent<NetworkHitSplatters>();
            if (hitSplatters == null)
            {
                hitSplatters = gameObject.AddComponent<NetworkHitSplatters>();
            }

            propShatter = GetComponent<NetworkPropShatter>();
            if (propShatter == null)
            {
                propShatter = gameObject.AddComponent<NetworkPropShatter>();
            }

            Transform entityRoot = NetworkDemoScene.EnsureEntityRoot("Network Entities");
            renderStateApplier.Configure(entityRegistry, prefabRegistry, entityRoot);
            hitscanTracers.Configure(prefabRegistry, entityRoot);
            renderStateApplier.ConfigureTracers(hitscanTracers);
            hitSplatters.Configure(entityRoot);
            renderStateApplier.ConfigureSplatters(hitSplatters);
            propShatter.Configure(entityRoot);
            renderStateApplier.ConfigureShatter(propShatter);
        }

        private void UpdateCameraTarget(uint localPlayerNetId)
        {
            if (followCamera == null)
            {
                return;
            }

            if (localPlayerNetId != 0 &&
                entityRegistry != null &&
                entityRegistry.TryGetByNetId(localPlayerNetId, out GameObject visual))
            {
                followCamera.SetTarget(visual.transform);
                return;
            }

            followCamera.SetTarget(null);
        }

        private static GameplayCatalogSyncOptions CreateGameplayCatalogSyncOptions()
        {
            return new GameplayCatalogSyncOptions
            {
                CacheDirectory = Path.Combine(
                    Application.persistentDataPath,
                    "NetworkExample",
                    "GameplayCatalogCache"),
            };
        }

        private static void ConfigurePhysicsBeforeStart(
            global::NetworkExample.Kernel.Kernel kernel)
        {
            var physicsConfig = new KernelPhysicsConfig
            {
                physics_simulation = 0,
                physics_workers = 0,
            };

            try
            {
                if (!kernel.SetPhysicsConfig(physicsConfig))
                {
                    throw new InvalidOperationException(
                        "Kernel_SetPhysicsConfig failed before client start.");
                }
            }
            catch (EntryPointNotFoundException)
            {
                // com.network-example.kernel 0.6.6 at commit 77c1679 has ABI 42
                // managed bindings, but its macOS export list omitted this symbol.
                // These values exactly match the ABI 42 native defaults, so this
                // compatibility path is behaviorally equivalent until the packaged
                // dylib is rebuilt with Kernel_SetPhysicsConfig exported.
                Debug.LogWarning(
                    "Native plugin does not export Kernel_SetPhysicsConfig; " +
                    "continuing with equivalent ABI 42 physics defaults " +
                    "(physics_simulation=0, physics_workers=0). Update the " +
                    "com.network-example.kernel native plugin to remove this fallback.");
            }
        }

        private static void WarnIfBufferFilled(uint count, int capacity, string bufferName)
        {
            if (count >= capacity)
            {
                Debug.LogWarning("Client " + bufferName + " buffer reached capacity " + capacity + ".");
            }
        }

        private void CompleteLocalActions(uint count)
        {
            int safeCount = SafeCount(count, localActionResults.Length);
            for (int index = 0; index < safeCount; ++index)
            {
                KernelLocalActionResult result = localActionResults[index];
                LogActionResultFailure(result);
                fireStallDiagnostic?.NoteActionResult(result.result, result.reason);
                inputSampler.ApplyActionResult(
                    result.action_instance_id,
                    result.result,
                    result.reason);
            }
        }

        /// <summary>
        /// The reason an action ended is the only evidence of why a burst stopped:
        /// a hit reaction cancelling it and a weapon that ran dry look identical
        /// from the outside.
        /// </summary>
        private void LogActionResultFailure(KernelLocalActionResult result)
        {
            if (!logActionResultFailures ||
                result.result == KernelLocalActionResultType.Accepted)
            {
                return;
            }

            string binding = inputSampler.TryGetActionBinding(
                result.action_instance_id,
                out KernelActionBinding actionBinding)
                ? actionBinding.ToString()
                : "unknown";
            Debug.Log(
                "Client local action " +
                result.action_instance_id +
                " (" +
                binding +
                ") ended: " +
                result.result +
                " (" +
                result.reason +
                ") at tick " +
                result.authoritative_tick +
                "; may restart while held = " +
                NetworkInputSampler.CanRestartWhileHeld(result.reason));
        }


        /// <summary>
        /// Reports a held trigger that has stopped producing shots, pairing what
        /// the sampler believes against the authoritative action state.
        /// </summary>
        private void ObserveFireStall(int safeRenderCount)
        {
            if (!logFireStalls || fireStallDiagnostic == null)
            {
                return;
            }

            bool hasState = TryGetLocalPlayerState(
                safeRenderCount,
                client.LocalPlayerNetId,
                out RenderEntityState localState);
            if (fireStallDiagnostic.Observe(
                    Time.unscaledDeltaTime,
                    inputSampler.IsFireHeld,
                    inputSampler.HeldFireActionInstanceId,
                    hasState,
                    localState.action,
                    localState.visual_flags,
                    inputSampler.OutstandingActionCount,
                    inputSampler.TotalActionsAllocated,
                    out string report))
            {
                Debug.LogWarning("Client " + report);
            }
        }

        private bool TryGetLocalPlayerState(
            int safeRenderCount,
            uint localPlayerNetId,
            out RenderEntityState found)
        {
            found = default;
            if (renderStates == null || localPlayerNetId == 0)
            {
                return false;
            }

            for (int index = 0; index < safeRenderCount; ++index)
            {
                if (renderStates[index].net_id == localPlayerNetId)
                {
                    found = renderStates[index];
                    return true;
                }
            }

            return false;
        }

        private static int SafeCount(uint count, int capacity)
        {
            return count > (uint)capacity ? capacity : (int)count;
        }

        private void LogDiagnosticEvents(uint eventCount)
        {
            if (!enableDiagnostics || events == null)
            {
                return;
            }

            int safeEventCount = eventCount > (uint)events.Length
                ? events.Length
                : (int)eventCount;
            for (int index = 0; index < safeEventCount; ++index)
            {
                KernelEvent kernelEvent = events[index];
                Debug.Log(
                    "Client diagnostic event[" +
                    index +
                    "] type=" +
                    kernelEvent.type +
                    " tick=" +
                    kernelEvent.tick +
                    " net_id=" +
                    kernelEvent.net_id +
                    " peer_id=" +
                    kernelEvent.peer_id +
                    " code=" +
                    kernelEvent.code +
                    " event_time_us=" +
                    kernelEvent.event_time_us +
                    " presentation_time_us=" +
                    kernelEvent.presentation_time_us);
            }
        }

        private void LogConnectionState()
        {
            NetworkClientConnectionState connectionState = client.ConnectionState;
            bool stateChanged = connectionState != lastConnectionState;
            lastConnectionState = connectionState;

            // Reaching ready is not one moment to be handled and forgotten. The
            // catalog the sync writes to disk can still be landing when the
            // connection first reports ready, so the configure below is retried
            // on later frames instead of being consumed with the state edge.
            if (connectionState == NetworkClientConnectionState.Ready)
            {
                TryConfigureSynchronizedCatalogWhileReady();
                return;
            }

            if (!stateChanged)
            {
                return;
            }

            if (connectionState == NetworkClientConnectionState.Failed)
            {
                Debug.LogError(
                    "Client gameplay catalog sync failed: " +
                    FormatCatalogSyncResult(client.CatalogSyncResult));
                return;
            }

            Debug.Log("Client connection state=" + connectionState);
        }

        /// <summary>
        /// Configures the synchronized catalog, retrying on every frame the
        /// connection stays ready until it succeeds once.
        /// </summary>
        /// <remarks>
        /// Without a catalog the sampler has no weapon loadout, and
        /// <see cref="Update"/> gates every input submission on
        /// <see cref="NetworkInputSampler.HasWeaponLoadout"/> -- so a client that
        /// gives up after one failed attempt keeps rendering the world and
        /// orbiting the camera while movement and fire do nothing at all, with
        /// one error line as the only sign. One attempt reads the cached bundle
        /// off disk and hashes it, so the retry is paced rather than run every
        /// frame: a failure that is never going to clear must not turn into a
        /// per-frame disk read for the rest of the session.
        /// </remarks>
        private void TryConfigureSynchronizedCatalogWhileReady()
        {
            if (synchronizedCatalogConfigured ||
                Time.unscaledTime < nextCatalogConfigureTime)
            {
                return;
            }

            nextCatalogConfigureTime =
                Time.unscaledTime + CatalogConfigureRetrySeconds;
            GameplayCatalogSyncResult syncResult = client.CatalogSyncResult;
            if (!ConfigureSynchronizedCatalog(syncResult))
            {
                return;
            }

            synchronizedCatalogConfigured = true;
            Debug.Log(
                "Client gameplay catalog sync ready cache_hit=" +
                syncResult.CacheHit +
                " memory_only=" +
                syncResult.MemoryOnly +
                " " +
                NetworkGameplayCatalogBundle.FormatLoadResult(syncResult.LoadResult));
            if (!string.IsNullOrEmpty(syncResult.CacheWarning))
            {
                Debug.LogWarning(
                    "Client gameplay catalog cache warning: " +
                    syncResult.CacheWarning);
            }
        }

        /// <summary>
        /// Reports why the synchronized catalog could not be configured, once per
        /// connection. The attempt now repeats every frame until it succeeds, so
        /// logging each failure would bury the console.
        /// </summary>
        private void LogCatalogConfigureFailure(string message)
        {
            if (catalogConfigureFailureLogged)
            {
                return;
            }

            catalogConfigureFailureLogged = true;
            Debug.LogError(message);
        }

        /// <summary>
        /// Reads what the client needs out of the catalog the server sent: the
        /// player's weapon loadout, and the skeleton manifests every rigged actor
        /// is drawn from.
        /// </summary>
        /// <remarks>
        /// The manifests are not optional. KernelSkeletonBinding resolves its
        /// bone layout through KernelSkeletonManifestCatalog, so with none loaded
        /// it cannot validate, KernelSkeletonPoseApplicator rejects every pose,
        /// and a rigged actor spawns correctly and then never moves a bone.
        /// Reading them from the synchronized bytes rather than from the packaged
        /// copy is what keeps the rig the client draws and the skeleton the server
        /// simulates the same version.
        /// </remarks>
        private bool ConfigureSynchronizedCatalog(GameplayCatalogSyncResult syncResult)
        {
            string diagnostic = null;
            if (gameplayCatalogSyncOptions == null ||
                !NetworkGameplayCatalogBundle.TryLoadSynchronizedBundle(
                    gameplayCatalogSyncOptions.CacheDirectory,
                    serverAddress,
                    syncResult.Manifest,
                    out byte[] bundleBytes,
                    out diagnostic))
            {
                LogCatalogConfigureFailure(
                    "Client could not read the synchronized gameplay catalog bundle: " +
                    (string.IsNullOrEmpty(diagnostic)
                        ? "no bundle bytes"
                        : diagnostic));
                return false;
            }

            if (!NetworkSkeletonManifests.TryLoad(
                    bundleBytes,
                    syncResult.Manifest.entry_path,
                    out string manifestError))
            {
                LogCatalogConfigureFailure(
                    "Client could not read skeleton manifests, so kernel poses will " +
                    "be rejected: " + manifestError);
                return false;
            }

            if (!NetworkGameplayCatalogBundle.TryReadPlayerWeaponLoadout(
                    bundleBytes,
                    syncResult.Manifest.entry_path,
                    out byte[] weaponIds,
                    out int activeWeaponSlot,
                    out diagnostic) ||
                !inputSampler.ConfigureWeaponLoadout(weaponIds, activeWeaponSlot))
            {
                LogCatalogConfigureFailure(
                    "Client could not configure the synchronized player weapon loadout: " +
                    (string.IsNullOrEmpty(diagnostic)
                        ? "invalid weapon slot configuration"
                        : diagnostic));
                return false;
            }

            ConfigureInstantWeaponTracers(bundleBytes, syncResult.Manifest.entry_path);
            ConfigureWeaponFireTriggerModes(bundleBytes, syncResult.Manifest.entry_path);
            ConfigureAgentNavMesh(bundleBytes, syncResult.Manifest.entry_path);
            agentColliderShapes.InvalidateCatalog();
            return true;
        }

        /// <summary>
        /// Hands the tracer component the instant weapons this session's catalog
        /// declares, so a hitscan or shotgun shot has a reach and a piece of art
        /// to be drawn with.
        /// </summary>
        /// <remarks>
        /// A warning rather than a failure: this is presentation only. Without it
        /// those two weapon types fire invisibly and everything else -- including
        /// all of their damage -- is unaffected.
        /// </remarks>
        private void ConfigureInstantWeaponTracers(byte[] bundleBytes, string entryPath)
        {
            if (hitscanTracers == null)
            {
                return;
            }

            if (!NetworkGameplayCatalogBundle.TryReadInstantWeaponPresentations(
                    bundleBytes,
                    entryPath,
                    out Dictionary<uint, NetworkInstantWeaponPresentation> weapons,
                    out string diagnostic))
            {
                Debug.LogWarning(
                    "Client could not read the catalog's instant weapons, so hitscan " +
                    "and shotgun fire will not be drawn: " + diagnostic,
                    this);
                return;
            }

            hitscanTracers.ConfigureWeapons(weapons);
        }

        /// <summary>
        /// Tells the local agent which weapons keep firing while held. Without it
        /// every weapon is treated as press-to-fire, which still fires, only a
        /// hold weapon at a lower rate.
        /// </summary>
        private void ConfigureWeaponFireTriggerModes(byte[] bundleBytes, string entryPath)
        {
            if (!NetworkGameplayCatalogBundle.TryReadWeaponFireTriggerModes(
                    bundleBytes,
                    entryPath,
                    out weaponFireTriggerModes,
                    out string diagnostic))
            {
                weaponFireTriggerModes = null;
                Debug.LogWarning(
                    "Client could not read the catalog's weapon trigger modes, so the " +
                    "local agent re-presses fire for every weapon: " + diagnostic,
                    this);
            }
        }

        /// <summary>
        /// Loads the navigation mesh the server's patrols use, so the local agent
        /// explores the same walkable area. Without it the agent follows and
        /// fights but does not explore.
        /// </summary>
        private void ConfigureAgentNavMesh(byte[] bundleBytes, string entryPath)
        {
            agentNavMesh = null;
            localAgent.NavMesh = null;
            if (!NetworkGameplayCatalogBundle.TryReadNavigationMesh(
                    bundleBytes, entryPath, out byte[] navMeshBytes, out string diagnostic) ||
                !DetourNavMesh.TryParse(navMeshBytes, out DetourNavMesh mesh, out diagnostic))
            {
                Debug.LogWarning(
                    "Client could not load the catalog's navigation mesh for the local agent: " +
                    diagnostic,
                    this);
                return;
            }

            agentNavMesh = new DetourNavMeshQuery(mesh);
            localAgent.NavMesh = agentNavMesh;
        }

        private static string FormatCatalogSyncResult(GameplayCatalogSyncResult result)
        {
            string message =
                "error=" +
                result.Error +
                " message=" +
                result.ErrorMessage;
            if (result.Manifest.bundle_size != 0)
            {
                message +=
                    " server_catalog_version=" +
                    result.Manifest.catalog_version +
                    " server_catalog_hash=" +
                    result.Manifest.catalog_hash.ToString("x16") +
                    " bundle_size=" +
                    result.Manifest.bundle_size;
            }

            if (!string.IsNullOrEmpty(result.CacheWarning))
            {
                message += " cache_warning=" + result.CacheWarning;
            }

            return message;
        }

        /// <summary>
        /// Reports where each rendered skeleton's pose came from. A client does not
        /// simulate replicated actors, so their legs can only come from the follower path
        /// replaying steps the server sent; when that produces nothing the kernel falls
        /// back to the rig's bind pose, and the difference is invisible on screen unless
        /// you know the rig's rest shape. This makes it readable instead.
        ///
        /// Logged once a second rather than deduplicated: poseTick advancing in step with
        /// the server is itself the evidence that the pose history is being sampled at
        /// render time, and a summary that never changes would hide exactly that.
        /// </summary>
        /// <summary>
        /// Reports each leg's extension: the hip-to-foot span as a fraction of
        /// the leg's own bone length. The denominator is the bone lengths, which
        /// only rotations change, so this is exact rather than an estimate.
        ///
        /// It matters because this rig rests at about 98% extension with only
        /// 18-25 degrees of knee bend, leaving a foot roughly 0.24-0.52 m of
        /// vertical travel before the leg locks straight. At 100% a two-bone
        /// chain has no bend plane left: the IK target is clamped to the reach
        /// limit so the foot stops tracking the ground, and the limb swings
        /// about the hip-foot axis instead of bending.
        /// </summary>
        private void LogLegReach()
        {
            if (!logLegReach || client == null ||
                Time.realtimeSinceStartup < nextLegReachLogTime)
            {
                return;
            }
            nextLegReachLogTime = Time.realtimeSinceStartup + 1f;

            if (skeletonPoseStates == null || entityRegistry == null)
            {
                return;
            }

            int count = skeletonPoseStates.StateCount >
                    (uint)skeletonPoseStates.States.Length
                ? skeletonPoseStates.States.Length
                : (int)skeletonPoseStates.StateCount;
            for (int index = 0; index < count; ++index)
            {
                uint netId = skeletonPoseStates.States[index].entity_net_id;
                if (!entityRegistry.TryGetByNetId(netId, out GameObject visual) ||
                    visual == null)
                {
                    continue;
                }

                var binding = visual.GetComponentInChildren<KernelSkeletonBinding>(true);
                if (binding == null || binding.Bones == null)
                {
                    continue;
                }

                float rootY = visual.transform.position.y;
                var summary = new System.Text.StringBuilder(160);
                summary.Append("net_id=").Append(netId)
                    .Append(" rootY=").Append(rootY.ToString("F2"));

                int clamped = 0;
                for (int bone = 0; bone < binding.Bones.Length; ++bone)
                {
                    Transform foot = binding.Bones[bone];
                    if (foot == null || !foot.name.EndsWith("_Foot"))
                    {
                        continue;
                    }

                    string prefix = foot.name.Substring(0, foot.name.Length - 5);
                    Transform hip = null;
                    Transform knee = null;
                    for (int probe = 0; probe < binding.Bones.Length; ++probe)
                    {
                        Transform other = binding.Bones[probe];
                        if (other == null)
                        {
                            continue;
                        }
                        if (other.name == prefix + "_Hip")
                        {
                            hip = other;
                        }
                        else if (other.name == prefix + "_Knee")
                        {
                            knee = other;
                        }
                    }
                    if (hip == null || knee == null)
                    {
                        continue;
                    }

                    float bones = Vector3.Distance(hip.position, knee.position) +
                        Vector3.Distance(knee.position, foot.position);
                    if (bones <= 0.0001f)
                    {
                        continue;
                    }

                    float extension =
                        Vector3.Distance(hip.position, foot.position) / bones;
                    bool locked = extension >= 0.995f;
                    if (locked)
                    {
                        ++clamped;
                    }

                    summary.Append("\n  ")
                        .Append(prefix.Replace("JNT_Leg", ""))
                        .Append(" dy=").Append((foot.position.y - rootY).ToString("F2"))
                        .Append(" ext=").Append((extension * 100f).ToString("F1"))
                        .Append('%')
                        .Append(locked ? "  LOCKED" : string.Empty);
                }

                Debug.Log(
                    "[LegReach] " + summary + "\n  locked=" + clamped);
            }
        }

        private void LogSkeletonPoseProvenance()
        {
            if (!logSkeletonPoseProvenance || client == null ||
                Time.realtimeSinceStartup < nextSkeletonPoseLogTime)
            {
                return;
            }
            nextSkeletonPoseLogTime = Time.realtimeSinceStartup + 1f;

            if (skeletonPoseStates == null)
            {
                return;
            }

            int count = skeletonPoseStates.StateCount >
                    (uint)skeletonPoseStates.States.Length
                ? skeletonPoseStates.States.Length
                : (int)skeletonPoseStates.StateCount;

            var summary = new System.Text.StringBuilder(128);
            summary.Append("skeletons=").Append(count)
                .Append(" status=").Append(skeletonPoseStates.Result.status);
            if (skeletonPoseStates.Result.status !=
                KernelConstants.SkeletonRenderStatusSuccess)
            {
                // The kernel drops the whole result when the buffer is short, which
                // freezes every rig at its last applied pose.
                summary.Append(" (needs states=")
                    .Append(skeletonPoseStates.Result.required_state_count)
                    .Append(" bones=")
                    .Append(skeletonPoseStates.Result.required_bone_transform_count)
                    .Append(')');
            }
            for (int index = 0; index < count; ++index)
            {
                KernelSkeletonRenderState pose = skeletonPoseStates.States[index];
                string kind =
                    (pose.pose_flags & KernelConstants.SkeletonPoseFlagProcedural) != 0
                        ? "PROC"
                        : (pose.pose_flags & KernelConstants.SkeletonPoseFlagBindPose) != 0
                            ? "BIND"
                            : "none";
                summary.Append("\n  net_id=").Append(pose.entity_net_id)
                    .Append(' ').Append(kind)
                    .Append(" bones=").Append(pose.bone_count)
                    .Append(" poseTick=").Append(pose.pose_tick)
                    .Append(" poseTimeUs=").Append(pose.pose_time_us);
                AppendFootHeights(summary, pose.entity_net_id);
            }

            Debug.Log("[SkeletonPose] " + summary);
        }

        /// <summary>
        /// Reports each foot bone's height RELATIVE TO THE ENTITY ROOT, which is
        /// the one measurement that separates the ways this can be wrong:
        ///
        ///   dy ~ 0 and changing   legs are stepping and seated on the ground
        ///   dy ~ 0 but constant   seated, but no step ever reached this client
        ///   dy ~ +2.7 constant    the rig is at its bind pose: the kernel seats
        ///                         the pose by dropping the root bone so the
        ///                         lowest bind foot sits AT the root, so an
        ///                         unseated rig floats by exactly that offset
        ///
        /// Root-relative rather than absolute so it does not need to know the
        /// terrain height under the actor.
        /// </summary>
        private void AppendFootHeights(System.Text.StringBuilder summary, uint netId)
        {
            if (entityRegistry == null ||
                !entityRegistry.TryGetByNetId(netId, out GameObject visual) ||
                visual == null)
            {
                summary.Append(" [no visual]");
                return;
            }

            var binding = visual.GetComponentInChildren<KernelSkeletonBinding>(true);
            if (binding == null || binding.Bones == null)
            {
                summary.Append(" [no binding]");
                return;
            }

            float rootY = visual.transform.position.y;
            summary.Append("\n    rootY=").Append(rootY.ToString("F2"))
                .Append(" leg dy/extension=");
            for (int index = 0; index < binding.Bones.Length; ++index)
            {
                Transform foot = binding.Bones[index];
                if (foot == null || !foot.name.EndsWith("_Foot"))
                {
                    continue;
                }

                summary.Append("  ")
                    .Append((foot.position.y - rootY).ToString("F2"));

                string prefix = foot.name.Substring(0, foot.name.Length - 5);
                Transform hip = null;
                Transform knee = null;
                for (int probe = 0; probe < binding.Bones.Length; ++probe)
                {
                    Transform bone = binding.Bones[probe];
                    if (bone == null)
                    {
                        continue;
                    }
                    if (bone.name == prefix + "_Hip")
                    {
                        hip = bone;
                    }
                    else if (bone.name == prefix + "_Knee")
                    {
                        knee = bone;
                    }
                }
                if (hip == null || knee == null)
                {
                    continue;
                }

                // Fraction of the leg's bone length the hip-to-foot span uses.
                // At 100% the two-bone chain is straight and has no bend plane
                // left, so the limb swings about the hip-foot axis instead of
                // bending. This rig rests near 98%.
                float bones = Vector3.Distance(hip.position, knee.position) +
                    Vector3.Distance(knee.position, foot.position);
                if (bones <= 0.0001f)
                {
                    continue;
                }

                float extension =
                    Vector3.Distance(hip.position, foot.position) / bones;
                summary.Append('/')
                    .Append((extension * 100f).ToString("F1"))
                    .Append(extension >= 0.995f ? "%!" : "%");
            }
        }

        private void LogDiagnosticRenderSummary(uint renderCount, int safeRenderCount)
        {
            if (!enableDiagnostics || renderStates == null || Time.realtimeSinceStartup < nextDiagnosticLogTime)
            {
                return;
            }

            nextDiagnosticLogTime =
                Time.realtimeSinceStartup + Mathf.Max(0.1f, diagnosticLogIntervalSeconds);

            int playerCount = 0;
            int enemyCount = 0;
            int projectileCount = 0;
            int otherCount = 0;
            for (int index = 0; index < safeRenderCount; ++index)
            {
                RenderEntityState state = renderStates[index];
                if (state.entity_type == KernelEntityType.Projectile)
                {
                    projectileCount++;
                }
                else if (state.entity_type == KernelEntityType.Actor &&
                    state.actor_type == KernelActorType.Agent)
                {
                    enemyCount++;
                }
                else if (state.entity_type == KernelEntityType.Actor &&
                    state.actor_type == KernelActorType.Player)
                {
                    playerCount++;
                }
                else
                {
                    otherCount++;
                }
            }

            Debug.Log(
                "Client diagnostic render summary raw=" +
                renderCount +
                " safe=" +
                safeRenderCount +
                " ready=" +
                client.IsReady +
                " peer=" +
                client.LocalPeerId +
                " local_player=" +
                client.LocalPlayerNetId +
                " players=" +
                playerCount +
                " enemies=" +
                enemyCount +
                " projectiles=" +
                projectileCount +
                " other=" +
                otherCount);

            int previewCount = Mathf.Min(safeRenderCount, 5);
            for (int index = 0; index < previewCount; ++index)
            {
                RenderEntityState state = renderStates[index];
                Debug.Log(
                    "Client diagnostic render state[" +
                    index +
                    "] entity_id=" +
                    state.entity_id +
                    " net_id=" +
                    state.net_id +
                    " type=" +
                    state.entity_type +
                    " owner_peer=" +
                    state.owner_peer +
                    " position=(" +
                    state.position.x.ToString("0.###") +
                    ", " +
                    state.position.y.ToString("0.###") +
                    ", " +
                    state.position.z.ToString("0.###") +
                    ")");
            }
        }

        private void WarnIfReadyWithoutRenderStates(int safeRenderCount)
        {
            if (!enableDiagnostics || client == null || !client.IsReady)
            {
                readyWithoutRenderSeconds = 0f;
                readyWithoutRenderWarningLogged = false;
                return;
            }

            if (safeRenderCount > 0)
            {
                readyWithoutRenderSeconds = 0f;
                readyWithoutRenderWarningLogged = false;
                return;
            }

            readyWithoutRenderSeconds += Time.unscaledDeltaTime;
            if (readyWithoutRenderWarningLogged ||
                readyWithoutRenderSeconds < Mathf.Max(0f, readyWithoutRenderWarningSeconds))
            {
                return;
            }

            readyWithoutRenderWarningLogged = true;
            Debug.LogWarning(
                "Client diagnostic: client is ready but has received zero render states for " +
                readyWithoutRenderSeconds.ToString("0.###") +
                " seconds. peer=" +
                client.LocalPeerId +
                " local_player=" +
                client.LocalPlayerNetId);
        }
    }
}
