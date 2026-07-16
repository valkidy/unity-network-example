using System;
using NetworkExample.Kernel;
using NetworkExample.Kernel.Host;
using NetworkExample.UnityDemo.Common;
using NetworkExample.UnityDemo.Input;
using NetworkExample.UnityDemo.Rendering;
using UnityEngine;

namespace NetworkExample.UnityDemo.Host
{
    [DisallowMultipleComponent]
    public sealed class HostModeRunner : MonoBehaviour
    {
        [SerializeField]
        private ushort port = 7777;

        [SerializeField]
        private int maxEvents = 256;

        [SerializeField]
        private int maxRenderStates = 256;

        [SerializeField]
        private int maxActionEvents = 128;

        [SerializeField]
        private bool enableVisualDebug = true;

        private NetworkHost host;
        private KernelEvent[] events;
        private RenderEntityState[] renderStates;
        private KernelLocalActionResult[] localActionResults;
        private KernelRemoteActionPresentationEvent[] remoteActionEvents;
        private NetworkInputSampler inputSampler;
        private NetworkRenderStateApplier renderStateApplier;
        private NetworkDebugView debugView;
        private readonly NetworkPresentationClock presentationClock = new NetworkPresentationClock();
        private bool started;
        private bool readinessLogged;
        private bool initialLocalJoinForwarded;

        private void Awake()
        {
            EnsureComponents();
            events = new KernelEvent[Mathf.Max(1, maxEvents)];
            renderStates = new RenderEntityState[Mathf.Max(1, maxRenderStates)];
            localActionResults = new KernelLocalActionResult[Mathf.Max(1, maxActionEvents)];
            remoteActionEvents =
                new KernelRemoteActionPresentationEvent[Mathf.Max(1, maxActionEvents)];
        }

        private void OnEnable()
        {
            try
            {
                NetworkKernelVersionLogger.Log();
                if (!NetworkGameplayCatalogBundle.TryLoadDefault(
                        out byte[] bundleBytes,
                        out string entryPath))
                {
                    return;
                }

                host = new NetworkHost();
                started = host.Start(
                    port,
                    bundleBytes,
                    entryPath,
                    out KernelGameplayCatalogLoadResult loadResult);
                if (!started)
                {
                    Debug.LogError("HostMode failed to start on port " + port);
                    host.Dispose();
                    host = null;
                    return;
                }

                Debug.Log(
                    "HostMode loaded gameplay catalog bundle " +
                    NetworkGameplayCatalogBundle.FormatLoadResult(loadResult));
                Debug.Log("HostMode listening on 127.0.0.1:" + port);
                ForwardInitialLocalPlayerJoinToGameServer();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                host?.Dispose();
                host = null;
                started = false;
            }
        }

        private void Update()
        {
            if (!started || host == null)
            {
                return;
            }

            uint eventCount = host.Update(Time.deltaTime, events);
            WarnIfBufferFilled(eventCount, events.Length, "event");
            MarkInitialLocalJoinForwardedFromEvents(eventCount);
            ForwardInitialLocalPlayerJoinToGameServer();

            uint localActionResultCount = host.Kernel.PollLocalActionResults(
                localActionResults);
            uint remoteActionEventCount = host.Kernel.PollRemoteActionPresentationEvents(
                remoteActionEvents);
            WarnIfBufferFilled(
                localActionResultCount,
                localActionResults.Length,
                "local action result");
            WarnIfBufferFilled(
                remoteActionEventCount,
                remoteActionEvents.Length,
                "remote action presentation");
            CompleteLocalActions(localActionResultCount);

            ActionIntent predictedIntent = default;
            if (host.IsLocalClientReady)
            {
                PlayerInput input = inputSampler.Sample();
                if (host.TrySubmitLocalInput(input))
                {
                    predictedIntent = input.action_intent;
                }
                else
                {
                    inputSampler.StopActionInput(input.action_intent.action_instance_id);
                }
            }

            ulong clientRenderTimeUs = presentationClock.Advance(Time.deltaTime);
            uint renderCount = host.GetRenderStatesAtTime(clientRenderTimeUs, renderStates);
            WarnIfBufferFilled(renderCount, renderStates.Length, "render state");
            int safeRenderCount = renderCount > (uint)renderStates.Length
                ? renderStates.Length
                : (int)renderCount;
            renderStateApplier.Apply(renderStates, safeRenderCount);
            renderStateApplier.ApplyLocalActionResults(
                host.LocalPlayerNetId,
                localActionResults,
                SafeCount(localActionResultCount, localActionResults.Length));
            renderStateApplier.ApplyRemoteActionPresentationEvents(
                remoteActionEvents,
                SafeCount(remoteActionEventCount, remoteActionEvents.Length));
            renderStateApplier.BeginPredictedLocalAction(
                host.LocalPlayerNetId,
                predictedIntent);

            if (debugView != null)
            {
                debugView.Capture(host.Kernel, renderStates, safeRenderCount);
            }

            if (!readinessLogged && host.IsLocalClientReady)
            {
                readinessLogged = true;
                Debug.Log(
                    "HostMode local player ready. peer=" +
                    host.LocalPeerId +
                    " player=" +
                    host.LocalPlayerNetId);
            }
        }

        private void OnDisable()
        {
            renderStateApplier?.Clear();
            inputSampler?.ResetSession();
            host?.Dispose();
            host = null;
            started = false;
            readinessLogged = false;
            initialLocalJoinForwarded = false;
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

            Transform entityRoot = NetworkDemoScene.EnsureEntityRoot("Network Entities");
            renderStateApplier.Configure(entityRegistry, prefabRegistry, entityRoot);
        }

        private void MarkInitialLocalJoinForwardedFromEvents(uint eventCount)
        {
            if (initialLocalJoinForwarded || host == null || !host.IsLocalClientReady)
            {
                return;
            }

            int safeEventCount = eventCount > (uint)events.Length
                ? events.Length
                : (int)eventCount;
            for (int index = 0; index < safeEventCount; ++index)
            {
                KernelEvent kernelEvent = events[index];
                if (kernelEvent.type == KernelEventType.PlayerJoined &&
                    kernelEvent.net_id == host.LocalPlayerNetId &&
                    kernelEvent.peer_id == host.LocalPeerId)
                {
                    initialLocalJoinForwarded = true;
                    return;
                }
            }
        }

        private void ForwardInitialLocalPlayerJoinToGameServer()
        {
            if (initialLocalJoinForwarded || host == null || !host.IsLocalClientReady)
            {
                return;
            }

            host.GameServer.HandleEvent(
                new KernelEvent
                {
                    type = KernelEventType.PlayerJoined,
                    net_id = host.LocalPlayerNetId,
                    peer_id = host.LocalPeerId,
                });
            initialLocalJoinForwarded = true;
        }

        private static void WarnIfBufferFilled(uint count, int capacity, string bufferName)
        {
            if (count >= capacity)
            {
                Debug.LogWarning("HostMode " + bufferName + " buffer reached capacity " + capacity + ".");
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
    }
}
