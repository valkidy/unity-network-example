using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    [DisallowMultipleComponent]
    public sealed class NetworkProjectileView : MonoBehaviour
    {
        public ulong EntityId { get; private set; }
        public uint ActionInstanceId { get; private set; }
        public uint ServerEntityId { get; private set; }
        public bool IsConfirmed => ServerEntityId != 0;

        public void ApplyKernelState(RenderEntityState state)
        {
            EntityId = state.entity_id;
            ActionInstanceId = state.action_instance_id;
            if (state.net_id != 0)
            {
                ServerEntityId = state.net_id;
            }

            gameObject.name = NameFor(state);
            transform.SetPositionAndRotation(
                ToVector3(state.position),
                ToQuaternion(state.rotation));
        }

        private static Vector3 ToVector3(KernelVec3 value)
        {
            return new Vector3(value.x, value.y, value.z);
        }

        private static Quaternion ToQuaternion(KernelQuat value)
        {
            return new Quaternion(value.x, value.y, value.z, value.w);
        }

        private static string NameFor(RenderEntityState state)
        {
            if (state.net_id != 0)
            {
                return "NetProjectile_" + state.net_id;
            }

            if (state.action_instance_id != 0)
            {
                return "PredictedProjectile_" + state.action_instance_id;
            }

            return "PredictedProjectile";
        }
    }
}
