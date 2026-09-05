using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    /// <summary>
    /// One instant-weapon shot, drawn as a line from the muzzle to wherever the
    /// shot ended.
    /// </summary>
    /// <remarks>
    /// The body mesh follows the same convention <see cref="NetworkProjectileView"/>
    /// stretches a beam along: built along local +Z with its near end on this
    /// object's origin, two units long so a localScale of half the span produces a
    /// mesh exactly as long as the shot. That is what lets an authored beam prefab
    /// be dropped in here unchanged.
    ///
    /// A tracer is pure presentation with no kernel entity behind it, so nothing
    /// drives its lifetime but this component: <see cref="Tick"/> is called by the
    /// pool that owns it rather than by Unity, which keeps the whole thing
    /// exercisable from an EditMode test where no Update runs.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class NetworkTracerView : MonoBehaviour
    {
        [SerializeField]
        [Tooltip(
            "The mesh the shot stretches. Built along local +Z with its near end " +
            "on this object's origin. Left empty, the first child is used.")]
        private Transform tracerBody;

        // The prefab authors the tracer's thickness; only its length comes from
        // the shot, so the girth is captured once and carried through every
        // rescale -- the same reason NetworkProjectileView captures it.
        private float authoredGirthX = 1f;
        private float authoredGirthZ = 1f;
        private bool girthCaptured;

        private float remainingSeconds;
        private float totalSeconds;

        public bool IsPlaying => remainingSeconds > 0f;

        /// <summary>
        /// Points the tracer down <paramref name="direction"/> from
        /// <paramref name="origin"/> and stretches it to <paramref name="length"/>.
        /// </summary>
        public void Play(
            Vector3 origin,
            Vector3 direction,
            float length,
            float durationSeconds)
        {
            if (direction.sqrMagnitude <= 1e-8f || length <= 0f || durationSeconds <= 0f)
            {
                Stop();
                return;
            }

            totalSeconds = durationSeconds;
            remainingSeconds = durationSeconds;
            transform.SetPositionAndRotation(
                origin,
                Quaternion.LookRotation(direction.normalized, Vector3.up));
            gameObject.SetActive(true);
            ApplySpan(length, 1f);
        }

        /// <summary>
        /// Ages the tracer by one frame. Returns false once it has expired, which
        /// is the pool's signal to take it back.
        /// </summary>
        public bool Tick(float deltaSeconds)
        {
            if (remainingSeconds <= 0f)
            {
                return false;
            }

            remainingSeconds -= Mathf.Max(0f, deltaSeconds);
            if (remainingSeconds <= 0f)
            {
                Stop();
                return false;
            }

            // Thinning rather than fading alpha: a tracer inherits whatever
            // material its prefab authored, and an opaque one has no alpha to
            // animate. Girth is always there to spend.
            ApplySpan(CurrentLength(), remainingSeconds / totalSeconds);
            return true;
        }

        public void Stop()
        {
            remainingSeconds = 0f;
            totalSeconds = 0f;
            gameObject.SetActive(false);
        }

        private float CurrentLength()
        {
            Transform body = ResolveBody();
            return body == null ? 0f : body.localPosition.z * 2f;
        }

        private void ApplySpan(float length, float girthScale)
        {
            Transform body = ResolveBody();
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

            float halfLength = length * 0.5f;
            body.localPosition = new Vector3(0f, 0f, halfLength);
            body.localScale = new Vector3(
                authoredGirthX * girthScale,
                halfLength,
                authoredGirthZ * girthScale);
        }

        private Transform ResolveBody()
        {
            if (tracerBody != null)
            {
                return tracerBody;
            }

            return transform.childCount > 0 ? transform.GetChild(0) : null;
        }
    }
}
