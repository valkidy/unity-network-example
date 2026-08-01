using NetworkExample.Kernel;
using NetworkExample.Kernel.Client;

namespace NetworkExample.UnityDemo.Items
{
    public sealed class ItemPropRequestSender
    {
        private ulong nextRequestId = 1;

        public KernelGameplayRequest CreateRequest(
            uint requesterPeer,
            uint instigatorNetId,
            KernelDomainAction action,
            ulong selectedItemInstanceId = 0,
            uint targetNetId = 0,
            uint requestedQuantity = 0,
            KernelVec3 placementPosition = default,
            KernelVec3 throwDirection = default)
        {
            return new KernelGameplayRequest
            {
                requester_peer = requesterPeer,
                request_id = AllocateRequestId(),
                instigator_net_id = instigatorNetId,
                domain_action = (byte)action,
                selected_item_instance_id = selectedItemInstanceId,
                target_net_id = targetNetId,
                requested_quantity = requestedQuantity,
                placement_position = placementPosition,
                throw_direction = throwDirection,
            };
        }

        public bool Submit(NetworkClient client, KernelGameplayRequest request)
        {
            return client != null &&
                client.IsReady &&
                !client.IsDisconnected &&
                request.request_id != 0 &&
                client.Kernel.SubmitGameplayRequest(request);
        }

        public void Reset()
        {
            nextRequestId = 1;
        }

        private ulong AllocateRequestId()
        {
            ulong requestId = nextRequestId++;
            if (requestId != 0)
            {
                return requestId;
            }

            requestId = nextRequestId++;
            return requestId == 0 ? 1 : requestId;
        }
    }
}
