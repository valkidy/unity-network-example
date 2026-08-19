using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    [DisallowMultipleComponent]
    public sealed class NetworkProjectileView : MonoBehaviour
    {
        [SerializeField]
        [Tooltip(
            "The mesh a beam stretches. Built along local +Z with its near end " +
            "on this object's origin, so only its length is driven at runtime. " +
            "Left empty, the first child is used.")]
        private Transform beamBody;

        public ulong EntityId { get; private set; }
        public uint ActionInstanceId { get; private set; }
        public uint ServerEntityId { get; private set; }
        public bool IsConfirmed => ServerEntityId != 0;

        // The prefab authors the beam's thickness; only its length is replicated,
        // so the girth is captured once and carried through every rescale.
        private float authoredGirthX = 1f;
        private float authoredGirthZ = 1f;
        private bool girthCaptured;

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
            ApplyBeamSpan(state);
        }

        /// <summary>
        /// Stretches a beam from its origin to the endpoint the server sent.
        /// <para>
        /// Only beams carry a non-zero <c>beam_end</c>; every other projectile is
        /// a point and leaves this alone. The endpoint is already cut short at
        /// whatever stopped the beam, so a beam resting against a wall ends at
        /// the wall rather than reaching through it. Rotation is not recomputed
        /// here -- the kernel derives the state's rotation from the same span and
        /// maps it onto local +Z, which is the axis the mesh is built along.
        /// </para>
        /// </summary>
        private void ApplyBeamSpan(RenderEntityState state)
        {
            // Zero means "not a beam", and it has to be tested on the endpoint
            // itself rather than on the span: for anything not sitting on the
            // world origin, an unset endpoint yields a span the length of the
            // entity's distance from it, which silently stretched every ordinary
            // projectile by however far from the origin it happened to be.
            Vector3 end = ToVector3(state.beam_end);
            if (end.sqrMagnitude <= 1e-8f)
            {
                return;
            }

            Vector3 span = end - ToVector3(state.position);
            if (span.sqrMagnitude <= 1e-8f)
            {
                return;
            }

            Transform body = ResolveBeamBody();
            if (body == null)
            {
                return;
            }

            if (!girthCaptured)
            {
                Vector3 authored = body.localScale;
                authoredGirthX = authored.x;
                authoredGirthZ = authored.z;
                girthCaptured = true;
            }

            // Unity's capsule and cylinder primitives are two units tall, so a
            // localScale.y of half the span produces a mesh exactly as long as
            // the beam. Offsetting by the same amount puts the near end on the
            // origin instead of the middle.
            float halfLength = span.magnitude * 0.5f;
            body.localPosition = new Vector3(0f, 0f, halfLength);
            body.localScale = new Vector3(authoredGirthX, halfLength, authoredGirthZ);
        }

        private Transform ResolveBeamBody()
        {
            if (beamBody != null)
            {
                return beamBody;
            }

            return transform.childCount > 0 ? transform.GetChild(0) : null;
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
