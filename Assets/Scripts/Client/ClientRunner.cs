using System;
using System.IO;
using NetworkExample.Kernel;
using NetworkExample.Kernel.Client;
using NetworkExample.UnityDemo.CameraSystem;
using NetworkExample.UnityDemo.Common;
using NetworkExample.UnityDemo.Input;
using NetworkExample.UnityDemo.Rendering;
using UnityEngine;

namespace NetworkExample.UnityDemo.Client
{
    [DisallowMultipleComponent]
    public sealed class ClientRunner : MonoBehaviour
    {
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

        private NetworkClient client;
        private KernelEvent[] events;
        private RenderEntityState[] renderStates;
        private KernelEntityLifecycleEvent[] lifecycleEvents;
        private KernelLocalActionResult[] localActionResults;
        private KernelRemoteActionPresentationEvent[] remoteActionEvents;
        private NetworkInputSampler inputSampler;
        private NetworkEntityRegistry entityRegistry;
        private NetworkRenderStateApplier renderStateApplier;
        private NetworkDebugView debugView;
        private ThirdPersonFollowCamera followCamera;
        private readonly NetworkPresentationClock presentationClock = new NetworkPresentationClock();
        private NetworkInputSubmissionClock inputSubmissionClock;
        private GameplayCatalogSyncOptions gameplayCatalogSyncOptions;
        private bool started;
        private bool readinessLogged;
        private float nextDiagnosticLogTime;
        private float readyWithoutRenderSeconds;
        private bool readyWithoutRenderWarningLogged;
        private NetworkClientConnectionState lastConnectionState;

        private void Awake()
        {
            EnsureComponents();
            inputSubmissionClock = new NetworkInputSubmissionClock(
                Mathf.Max(1f, inputSubmissionRateHz));
            events = new KernelEvent[Mathf.Max(1, maxEvents)];
            renderStates = new RenderEntityState[Mathf.Max(1, maxRenderStates)];
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

            KernelActionIntent predictedIntent = default;
            if (client.IsReady &&
                inputSampler.HasWeaponLoadout &&
                inputSubmissionClock.ShouldSubmit(Time.unscaledDeltaTime))
            {
                KernelPlayerInput input = inputSampler.Sample();
                if (client.TrySubmitInput(input))
                {
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
            }

            uint eventCount = client.Update(Time.unscaledDeltaTime, events);
            WarnIfBufferFilled(eventCount, events.Length, "event");
            LogDiagnosticEvents(eventCount);
            LogConnectionState();

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
                inputSubmissionClock.Reset();
                started = false;
                return;
            }

            ulong clientRenderTimeUs = presentationClock.Advance(
                Time.unscaledDeltaTime);
            uint renderCount = client.GetRenderStatesAtTime(clientRenderTimeUs, renderStates);
            WarnIfBufferFilled(renderCount, renderStates.Length, "render state");
            int safeRenderCount = renderCount > (uint)renderStates.Length
                ? renderStates.Length
                : (int)renderCount;
            LogDiagnosticRenderSummary(renderCount, safeRenderCount);
            WarnIfReadyWithoutRenderStates(safeRenderCount);
            renderStateApplier.Apply(renderStates, safeRenderCount);
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

            if (debugView != null)
            {
                debugView.Capture(client.Kernel, renderStates, safeRenderCount);
            }

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
            followCamera?.SetTarget(null);
            renderStateApplier?.Clear();
            inputSampler?.ResetSession();
            client?.Dispose();
            client = null;
            gameplayCatalogSyncOptions = null;
            started = false;
            readinessLogged = false;
            nextDiagnosticLogTime = 0f;
            readyWithoutRenderSeconds = 0f;
            readyWithoutRenderWarningLogged = false;
            lastConnectionState = NetworkClientConnectionState.Idle;
            presentationClock.Reset();
            inputSubmissionClock?.Reset();
        }

        private void EnsureComponents()
        {
            followCamera = NetworkDemoScene.EnsureDefaultView();

            inputSampler = GetComponent<NetworkInputSampler>();
            if (inputSampler == null)
            {
                inputSampler = gameObject.AddComponent<NetworkInputSampler>();
            }
            inputSampler.SetViewTransform(followCamera.transform);

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
            Transform entityRoot = NetworkDemoScene.EnsureEntityRoot("Network Entities");
            renderStateApplier.Configure(entityRegistry, prefabRegistry, entityRoot);
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
                inputSampler.CompleteAction(result.action_instance_id);
                if (result.result != KernelLocalActionResultType.Accepted)
                {
                    inputSampler.StopActionInput(result.action_instance_id);
                }
            }
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
            if (connectionState == lastConnectionState)
            {
                return;
            }

            lastConnectionState = connectionState;
            if (connectionState == NetworkClientConnectionState.Ready)
            {
                GameplayCatalogSyncResult syncResult = client.CatalogSyncResult;
                if (!ConfigureInputWeaponLoadout(syncResult))
                {
                    return;
                }
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

        private bool ConfigureInputWeaponLoadout(GameplayCatalogSyncResult syncResult)
        {
            string diagnostic = null;
            if (gameplayCatalogSyncOptions == null ||
                !NetworkGameplayCatalogBundle.TryLoadSynchronizedBundle(
                    gameplayCatalogSyncOptions.CacheDirectory,
                    serverAddress,
                    syncResult.Manifest,
                    out byte[] bundleBytes,
                    out diagnostic) ||
                !NetworkGameplayCatalogBundle.TryReadPlayerWeaponLoadout(
                    bundleBytes,
                    syncResult.Manifest.entry_path,
                    out byte[] weaponIds,
                    out int activeWeaponSlot,
                    out diagnostic) ||
                !inputSampler.ConfigureWeaponLoadout(weaponIds, activeWeaponSlot))
            {
                Debug.LogError(
                    "Client could not configure the synchronized player weapon loadout: " +
                    (string.IsNullOrEmpty(diagnostic)
                        ? "invalid weapon slot configuration"
                        : diagnostic));
                return false;
            }

            return true;
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
