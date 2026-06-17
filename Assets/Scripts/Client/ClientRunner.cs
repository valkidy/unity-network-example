using System;
using NetworkExample.Kernel;
using NetworkExample.Kernel.Client;
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
        private bool enableDiagnostics = true;

        [SerializeField]
        private float diagnosticLogIntervalSeconds = 1.0f;

        [SerializeField]
        private float readyWithoutRenderWarningSeconds = 1.0f;

        [SerializeField]
        private bool enableVisualDebug = true;

        private NetworkClient client;
        private KernelEvent[] events;
        private RenderEntityState[] renderStates;
        private NetworkInputSampler inputSampler;
        private NetworkRenderStateApplier renderStateApplier;
        private NetworkDebugView debugView;
        private readonly NetworkPresentationClock presentationClock = new NetworkPresentationClock();
        private bool started;
        private bool readinessLogged;
        private float nextDiagnosticLogTime;
        private float readyWithoutRenderSeconds;
        private bool readyWithoutRenderWarningLogged;

        private void Awake()
        {
            EnsureComponents();
            events = new KernelEvent[Mathf.Max(1, maxEvents)];
            renderStates = new RenderEntityState[Mathf.Max(1, maxRenderStates)];
        }

        private void OnEnable()
        {
            try
            {
                NetworkKernelVersionLogger.Log();
                client = new NetworkClient();
                if (!NetworkGameplayCatalogBundle.TryLoadDefault(
                        out byte[] bundleBytes,
                        out string entryPath))
                {
                    client.Dispose();
                    client = null;
                    return;
                }

                if (!client.LoadGameplayCatalogFromMemory(
                        bundleBytes,
                        entryPath,
                        out KernelGameplayCatalogLoadResult loadResult))
                {
                    Debug.LogError(
                        "Client failed to load gameplay catalog bundle '" +
                        entryPath +
                        "': " +
                        NetworkGameplayCatalogBundle.FormatLoadResult(loadResult));
                    client.Dispose();
                    client = null;
                    return;
                }

                Debug.Log(
                    "Client loaded gameplay catalog bundle " +
                    NetworkGameplayCatalogBundle.FormatLoadResult(loadResult));

                started = client.Start(serverAddress);
                if (!started)
                {
                    Debug.LogError("Client failed to start for " + serverAddress);
                    client.Dispose();
                    client = null;
                    return;
                }

                Debug.Log("Client connecting to " + serverAddress);
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

            uint eventCount = client.Update(Time.deltaTime, events);
            WarnIfBufferFilled(eventCount, events.Length, "event");
            LogDiagnosticEvents(eventCount);

            if (client.IsReady)
            {
                PlayerInput input = inputSampler.Sample();
                client.TrySubmitInput(input);
            }

            ulong clientRenderTimeUs = presentationClock.Advance(Time.deltaTime);
            uint renderCount = client.GetRenderStatesAtTime(clientRenderTimeUs, renderStates);
            WarnIfBufferFilled(renderCount, renderStates.Length, "render state");
            int safeRenderCount = renderCount > (uint)renderStates.Length
                ? renderStates.Length
                : (int)renderCount;
            LogDiagnosticRenderSummary(renderCount, safeRenderCount);
            WarnIfReadyWithoutRenderStates(safeRenderCount);
            renderStateApplier.Apply(renderStates, safeRenderCount);

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
            renderStateApplier?.Clear();
            client?.Dispose();
            client = null;
            started = false;
            readinessLogged = false;
            nextDiagnosticLogTime = 0f;
            readyWithoutRenderSeconds = 0f;
            readyWithoutRenderWarningLogged = false;
            presentationClock.Reset();
        }

        private void EnsureComponents()
        {
            NetworkDemoScene.EnsureDefaultView();

            inputSampler = GetComponent<NetworkInputSampler>();
            if (inputSampler == null)
            {
                inputSampler = gameObject.AddComponent<NetworkInputSampler>();
            }

            NetworkEntityRegistry entityRegistry = GetComponent<NetworkEntityRegistry>();
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

        private static void WarnIfBufferFilled(uint count, int capacity, string bufferName)
        {
            if (count >= capacity)
            {
                Debug.LogWarning("Client " + bufferName + " buffer reached capacity " + capacity + ".");
            }
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
                switch (renderStates[index].entity_type)
                {
                    case KernelEntityType.Player:
                        playerCount++;
                        break;
                    case KernelEntityType.Enemy:
                        enemyCount++;
                        break;
                    case KernelEntityType.Projectile:
                        projectileCount++;
                        break;
                    default:
                        otherCount++;
                        break;
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

            readyWithoutRenderSeconds += Time.deltaTime;
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
