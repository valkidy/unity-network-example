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

        [SerializeField]
        [Tooltip(
            "Stretches the body from where this projectile was first seen to " +
            "where it is now, like a column of light falling from the sky. " +
            "For projectiles that are drawn by their trail, not their tip.")]
        private bool spanFromFirstPosition;

        [SerializeField]
        [Tooltip(
            "Points local +Z along the replicated velocity instead of using the " +
            "replicated rotation. For bodies that fall or fly nose first.")]
        private bool faceVelocity;

        public ulong EntityId { get; private set; }
        public uint ActionInstanceId { get; private set; }
        public uint ServerEntityId { get; private set; }
        public bool IsConfirmed => ServerEntityId != 0;

        // The prefab authors the beam's thickness; only its length is replicated,
        // so the girth is captured once and carried through every rescale.
        private float authoredGirthX = 1f;
        private float authoredGirthZ = 1f;
        private bool girthCaptured;
        private bool firstPositionCaptured;
        private Vector3 firstPosition;

        public bool SpanFromFirstPosition
        {
            get => spanFromFirstPosition;
            set => spanFromFirstPosition = value;
        }

        public bool FaceVelocity
        {
            get => faceVelocity;
            set => faceVelocity = value;
        }

        public void ApplyKernelState(RenderEntityState state)
        {
            EntityId = state.entity_id;
            ActionInstanceId = state.action_instance_id;
            if (state.net_id != 0)
            {
                ServerEntityId = state.net_id;
            }

            gameObject.name = NameFor(state);
            Vector3 position = ToVector3(state.position);
            if (!firstPositionCaptured)
            {
                firstPosition = position;
                firstPositionCaptured = true;
            }

            if (spanFromFirstPosition)
            {
                ApplyColumnSpan(position);
                return;
            }

            Quaternion rotation = ToQuaternion(state.rotation);
            Vector3 velocity = ToVector3(state.velocity);
            if (faceVelocity && velocity.sqrMagnitude > 1e-6f)
            {
                rotation = Quaternion.LookRotation(velocity.normalized, UpFor(velocity));
            }

            transform.SetPositionAndRotation(position, rotation);
            ApplyBeamSpan(state);
        }

        /// <summary>
        /// Draws the body from the first position this view saw back up to the
        /// current one. The render state carries no start point for this, so the
        /// view's own first sighting stands in for it -- which is the drop height
        /// for anything first seen at spawn.
        /// </summary>
        private void ApplyColumnSpan(Vector3 position)
        {
            Vector3 span = firstPosition - position;
            Quaternion rotation = span.sqrMagnitude > 1e-8f
                ? Quaternion.LookRotation(span.normalized, UpFor(span))
                : transform.rotation;
            transform.SetPositionAndRotation(position, rotation);
            StretchBody(span.magnitude);
        }

        // LookRotation needs an up that is not parallel to the direction, and a
        // falling body's direction is often straight down.
        private static Vector3 UpFor(Vector3 direction)
        {
            return Mathf.Abs(Vector3.Dot(direction.normalized, Vector3.up)) > 0.99f
                ? Vector3.forward
                : Vector3.up;
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

            StretchBody(span.magnitude);
        }

        private void StretchBody(float length)
        {
            if (length <= 1e-4f)
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
            float halfLength = length * 0.5f;
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
