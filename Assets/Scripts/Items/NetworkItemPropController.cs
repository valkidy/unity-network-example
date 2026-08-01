using System.Collections.Generic;
using NetworkExample.Kernel;
using NetworkExample.Kernel.Client;
using UnityEngine;

namespace NetworkExample.UnityDemo.Items
{
    [DisallowMultipleComponent]
    public sealed class NetworkItemPropController : MonoBehaviour
    {
        [SerializeField]
        [Min(1)]
        private int maxOwnedContainers = 8;

        [SerializeField]
        [Min(1)]
        private int maxGameplayOutcomes = 32;

        [SerializeField]
        [Min(1)]
        private int maxInventoryDeltas = 64;

        [SerializeField]
        [Min(0.1f)]
        private float pickupRange = 3f;

        [SerializeField]
        private bool enableDiagnostics = true;

        private readonly Dictionary<ulong, InventoryContainerCache> containerCaches =
            new Dictionary<ulong, InventoryContainerCache>();
        private readonly HashSet<ulong> seenContainerIds = new HashSet<ulong>();
        private readonly List<ulong> containerIdsToRemove = new List<ulong>();
        private readonly List<KernelItemInstanceView> flattenedItems =
            new List<KernelItemInstanceView>();
        private readonly Dictionary<ulong, PendingRequest> pendingRequests =
            new Dictionary<ulong, PendingRequest>();
        private readonly LocalInventorySelectionModel selection =
            new LocalInventorySelectionModel();
        private readonly ItemPropRequestSender requestSender =
            new ItemPropRequestSender();

        private KernelInventoryContainerView[] ownedContainers;
        private KernelGameplayRequestOutcome[] gameplayOutcomes;
        private KernelInventoryDelta[] inventoryDeltas;
        private NetworkItemPropInputSampler inputSampler;
        private Transform viewTransform;
        private bool ownedContainerCapacityWarningLogged;
        private bool inventoryDeltaCapacityWarningLogged;
        private bool gameplayOutcomeCapacityWarningLogged;

        public LocalInventorySelectionModel Selection => selection;

        private void Awake()
        {
            EnsureBuffers();
        }

        private void OnDisable()
        {
            ResetSession();
        }

        public void Configure(
            NetworkItemPropInputSampler sampler,
            Transform cameraTransform)
        {
            inputSampler = sampler;
            viewTransform = cameraTransform;
        }

        public void UpdateAuthoritativeState(NetworkClient client)
        {
            EnsureBuffers();
            if (client == null || !client.IsReady || client.IsDisconnected)
            {
                return;
            }

            PollGameplayOutcomes(client);
            RefreshInventory(client);
        }

        public void ProcessInput(
            NetworkClient client,
            RenderEntityState[] renderStates,
            int renderStateCount)
        {
            if (inputSampler == null)
            {
                return;
            }

            ItemPropInputCommand commands = inputSampler.SampleCommands();
            if (commands == ItemPropInputCommand.None)
            {
                return;
            }

            if ((commands & ItemPropInputCommand.SelectNextItem) != 0)
            {
                SelectNextItem();
            }

            if (client == null || !client.IsReady || client.IsDisconnected)
            {
                LogWarning("Item input ignored because the network client is not ready.");
                return;
            }

            if ((commands & ItemPropInputCommand.Use) != 0)
            {
                SubmitSelectedItemRequest(client, KernelDomainAction.Consume);
            }

            if ((commands & ItemPropInputCommand.Throw) != 0)
            {
                SubmitThrowRequest(client);
            }

            if ((commands & ItemPropInputCommand.Pickup) != 0)
            {
                SubmitPickupRequest(client, renderStates, renderStateCount);
            }
        }

        public void ResetSession()
        {
            containerCaches.Clear();
            seenContainerIds.Clear();
            containerIdsToRemove.Clear();
            flattenedItems.Clear();
            pendingRequests.Clear();
            selection.Clear();
            requestSender.Reset();
            inputSampler?.ResetSession();
            ownedContainerCapacityWarningLogged = false;
            inventoryDeltaCapacityWarningLogged = false;
            gameplayOutcomeCapacityWarningLogged = false;
        }

        public static bool IsContainerReady(KernelInventoryContainerView container)
        {
            return (KernelInventorySyncState)container.sync_state ==
                KernelInventorySyncState.Ready;
        }

        private void EnsureBuffers()
        {
            int containerCapacity = Mathf.Max(1, maxOwnedContainers);
            if (ownedContainers == null || ownedContainers.Length != containerCapacity)
            {
                ownedContainers = new KernelInventoryContainerView[containerCapacity];
            }

            int outcomeCapacity = Mathf.Max(1, maxGameplayOutcomes);
            if (gameplayOutcomes == null || gameplayOutcomes.Length != outcomeCapacity)
            {
                gameplayOutcomes = new KernelGameplayRequestOutcome[outcomeCapacity];
            }

            int deltaCapacity = Mathf.Max(1, maxInventoryDeltas);
            if (inventoryDeltas == null || inventoryDeltas.Length != deltaCapacity)
            {
                inventoryDeltas = new KernelInventoryDelta[deltaCapacity];
            }
        }

        private void PollGameplayOutcomes(NetworkClient client)
        {
            uint count = client.Kernel.PollGameplayRequestOutcomes(gameplayOutcomes);
            WarnOnceWhenCapacityReached(
                count,
                gameplayOutcomes.Length,
                "gameplay outcome",
                ref gameplayOutcomeCapacityWarningLogged);

            int safeCount = SafeCount(count, gameplayOutcomes.Length);
            for (int index = 0; index < safeCount; ++index)
            {
                KernelGameplayRequestOutcome outcome = gameplayOutcomes[index];
                pendingRequests.TryGetValue(
                    outcome.request_id,
                    out PendingRequest pending);
                pendingRequests.Remove(outcome.request_id);

                var status = (KernelGameplayRequestStatus)outcome.status;
                var action = (KernelDomainAction)outcome.domain_action;
                var rejection =
                    (KernelGameplayRequestRejectionReason)outcome.rejection_reason;
                string message =
                    "Item action outcome request=" + outcome.request_id +
                    " requested_action=" + pending.action +
                    " committed_action=" + action +
                    " status=" + status +
                    " rejection=" + rejection +
                    " requested_item=" + pending.itemInstanceId +
                    " requested_target=" + pending.targetNetId +
                    " committed_item=" + outcome.item_instance_id +
                    " committed_prop=" + outcome.prop_entity_id +
                    " quantity=" + outcome.committed_quantity;
                if (status == KernelGameplayRequestStatus.Rejected)
                {
                    LogWarning(message);
                }
                else
                {
                    Log(message);
                }
            }
        }

        private void RefreshInventory(NetworkClient client)
        {
            uint rawContainerCount = client.Kernel.CopyOwnedInventoryContainers(
                client.LocalPlayerNetId,
                ownedContainers);
            WarnOnceWhenCapacityReached(
                rawContainerCount,
                ownedContainers.Length,
                "owned inventory container",
                ref ownedContainerCapacityWarningLogged);

            seenContainerIds.Clear();
            bool inventoryChanged = false;
            int containerCount = SafeCount(rawContainerCount, ownedContainers.Length);
            for (int index = 0; index < containerCount; ++index)
            {
                KernelInventoryContainerView container = ownedContainers[index];
                if (container.inventory_container_id == 0)
                {
                    continue;
                }

                ulong containerId = container.inventory_container_id;
                seenContainerIds.Add(containerId);
                if (!containerCaches.TryGetValue(
                        containerId,
                        out InventoryContainerCache cache))
                {
                    cache = new InventoryContainerCache();
                    containerCaches.Add(containerId, cache);
                }

                if (!IsContainerReady(container))
                {
                    if (cache.ready || cache.itemCount > 0)
                    {
                        cache.ready = false;
                        cache.itemCount = 0;
                        inventoryChanged = true;
                    }
                    continue;
                }

                uint deltaCount = client.Kernel.PollInventoryDeltas(
                    containerId,
                    inventoryDeltas);
                WarnOnceWhenCapacityReached(
                    deltaCount,
                    inventoryDeltas.Length,
                    "inventory delta",
                    ref inventoryDeltaCapacityWarningLogged);

                bool requiresSnapshot = !cache.ready ||
                    deltaCount > 0 ||
                    cache.revision != container.revision;
                if (!requiresSnapshot)
                {
                    continue;
                }

                int requiredCapacity = Mathf.Max(1, (int)container.slot_capacity);
                if (cache.items == null || cache.items.Length != requiredCapacity)
                {
                    cache.items = new KernelItemInstanceView[requiredCapacity];
                }

                uint rawItemCount = client.Kernel.CopyInventorySlots(
                    containerId,
                    cache.items);
                cache.itemCount = SafeCount(rawItemCount, cache.items.Length);
                cache.revision = container.revision;
                cache.ready = true;
                inventoryChanged = true;
            }

            containerIdsToRemove.Clear();
            foreach (KeyValuePair<ulong, InventoryContainerCache> pair in containerCaches)
            {
                if (!seenContainerIds.Contains(pair.Key))
                {
                    containerIdsToRemove.Add(pair.Key);
                }
            }

            for (int index = 0; index < containerIdsToRemove.Count; ++index)
            {
                containerCaches.Remove(containerIdsToRemove[index]);
                inventoryChanged = true;
            }
            containerIdsToRemove.Clear();

            if (!inventoryChanged)
            {
                return;
            }

            flattenedItems.Clear();
            foreach (KeyValuePair<ulong, InventoryContainerCache> pair in containerCaches)
            {
                InventoryContainerCache cache = pair.Value;
                if (!cache.ready || cache.items == null)
                {
                    continue;
                }

                for (int itemIndex = 0; itemIndex < cache.itemCount; ++itemIndex)
                {
                    flattenedItems.Add(cache.items[itemIndex]);
                }
            }

            selection.ReplaceItems(flattenedItems);
            Log("Inventory refreshed item_count=" + selection.ItemCount + ".");
            LogSelectedItem("Current item");
        }

        private void SelectNextItem()
        {
            if (!selection.SelectNext(out KernelItemInstanceView item))
            {
                LogWarning("Select item ignored because the local inventory is empty.");
                return;
            }

            Log("Selected next item " + FormatItem(item) + ".");
        }

        private void SubmitSelectedItemRequest(
            NetworkClient client,
            KernelDomainAction action)
        {
            if (!selection.TryGetSelected(out KernelItemInstanceView item))
            {
                LogWarning(action + " ignored because no inventory item is selected.");
                return;
            }

            KernelGameplayRequest request = requestSender.CreateRequest(
                client.LocalPeerId,
                client.LocalPlayerNetId,
                action,
                selectedItemInstanceId: item.item_instance_id);
            Submit(client, request, action);
        }

        private void SubmitThrowRequest(NetworkClient client)
        {
            if (!selection.TryGetSelected(out KernelItemInstanceView item))
            {
                LogWarning("Throw ignored because no inventory item is selected.");
                return;
            }

            Vector3 direction = viewTransform == null
                ? Vector3.zero
                : viewTransform.forward;
            if (!ItemPropTargetSelector.IsFiniteNonZero(direction))
            {
                LogWarning("Throw ignored because the camera forward direction is invalid.");
                return;
            }

            direction.Normalize();
            KernelGameplayRequest request = requestSender.CreateRequest(
                client.LocalPeerId,
                client.LocalPlayerNetId,
                KernelDomainAction.Throw,
                selectedItemInstanceId: item.item_instance_id,
                requestedQuantity: 1,
                throwDirection: new KernelVec3(
                    direction.x,
                    direction.y,
                    direction.z));
            Submit(client, request, KernelDomainAction.Throw);
        }

        private void SubmitPickupRequest(
            NetworkClient client,
            RenderEntityState[] renderStates,
            int renderStateCount)
        {
            Vector3 direction = viewTransform == null
                ? Vector3.zero
                : viewTransform.forward;
            if (!ItemPropTargetSelector.TrySelectPickupTarget(
                    renderStates,
                    renderStateCount,
                    client.LocalPlayerNetId,
                    direction,
                    pickupRange,
                    out RenderEntityState target))
            {
                LogWarning(
                    "Pickup ignored because no placed item-backed prop is " +
                    "within " + pickupRange.ToString("0.###") +
                    " meters in front of the player.");
                return;
            }

            KernelGameplayRequest request = requestSender.CreateRequest(
                client.LocalPeerId,
                client.LocalPlayerNetId,
                KernelDomainAction.Pickup,
                selectedItemInstanceId: target.item_instance_id,
                targetNetId: target.net_id);
            Submit(client, request, KernelDomainAction.Pickup);
        }

        private void Submit(
            NetworkClient client,
            KernelGameplayRequest request,
            KernelDomainAction action)
        {
            if (!requestSender.Submit(client, request))
            {
                LogWarning(
                    "Item action submission failed request=" + request.request_id +
                    " action=" + action +
                    " item=" + request.selected_item_instance_id +
                    " target=" + request.target_net_id + ".");
                return;
            }

            pendingRequests[request.request_id] = new PendingRequest(
                action,
                request.selected_item_instance_id,
                request.target_net_id);
            Log(
                "Item action submitted request=" + request.request_id +
                " action=" + action +
                " item=" + request.selected_item_instance_id +
                " target=" + request.target_net_id +
                " quantity=" + request.requested_quantity + ".");
        }

        private void LogSelectedItem(string prefix)
        {
            if (selection.TryGetSelected(out KernelItemInstanceView item))
            {
                Log(prefix + " " + FormatItem(item) + ".");
            }
            else
            {
                Log(prefix + " none.");
            }
        }

        private static string FormatItem(KernelItemInstanceView item)
        {
            return "container=" + item.inventory_container_id +
                " slot=" + item.slot +
                " instance=" + item.item_instance_id +
                " template=" + item.item_template_id +
                " quantity=" + item.quantity;
        }

        private void WarnOnceWhenCapacityReached(
            uint count,
            int capacity,
            string bufferName,
            ref bool warningLogged)
        {
            if (warningLogged || count < capacity)
            {
                return;
            }

            warningLogged = true;
            LogWarning(
                "Item system " + bufferName +
                " buffer reached capacity " + capacity + ".");
        }

        private static int SafeCount(uint count, int capacity)
        {
            return count > (uint)capacity ? capacity : (int)count;
        }

        private void Log(string message)
        {
            if (enableDiagnostics)
            {
                Debug.Log(message, this);
            }
        }

        private void LogWarning(string message)
        {
            if (enableDiagnostics)
            {
                Debug.LogWarning(message, this);
            }
        }

        private sealed class InventoryContainerCache
        {
            public KernelItemInstanceView[] items;
            public int itemCount;
            public ulong revision;
            public bool ready;
        }

        private readonly struct PendingRequest
        {
            public readonly KernelDomainAction action;
            public readonly ulong itemInstanceId;
            public readonly uint targetNetId;

            public PendingRequest(
                KernelDomainAction action,
                ulong itemInstanceId,
                uint targetNetId)
            {
                this.action = action;
                this.itemInstanceId = itemInstanceId;
                this.targetNetId = targetNetId;
            }
        }
    }
}
