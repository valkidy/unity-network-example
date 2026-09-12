using UnityEngine;
using UnityEngine.InputSystem;

namespace NetworkExample.UnityDemo.CameraSystem
{
    /// <summary>
    /// One camera framing: how far back the camera sits, how high it looks, how
    /// far off the shoulder it rides and how wide it sees.
    /// </summary>
    /// <remarks>
    /// The four values are not independent knobs to taste. Distance and field of
    /// view trade against each other -- the same on-screen character size can be
    /// produced by a near camera with a narrow lens or a far one with a wide lens,
    /// and only the background separates them. So these were derived by fixing the
    /// character's share of the frame height first and solving for distance:
    /// <c>followDistance = actorHeight / (2 * frameFraction * tan(fieldOfView/2))</c>.
    /// The wizard-cat player silhouette measures 1.90m tall (rendered and counted
    /// in pixels, not inferred from the prefab's bind-pose bounds -- those are the
    /// root bone's serialized AABB and disagree with the posed character by more
    /// than a third). At that height the hip profile reproduces the ~40% frame
    /// share of the traversal reference exactly.
    ///
    /// The aim profile deliberately does not. Matching the reference's ~104% share
    /// would need <c>followDistance 2.1</c>, and that number was derived from a
    /// tall human soldier -- wizard-cat is a short body under a wide-brimmed
    /// conical hat, so the same share fills the frame with hat and buries the
    /// weapon and arms the player needs to see while aiming. Backing off to 3.4
    /// lands at a ~64% share, which keeps the body readable and still leaves the
    /// right half of the frame clear for the aim area, exactly as the reference
    /// does.
    ///
    /// <c>pivotHeight</c> is the point the camera actually looks at, so it has to
    /// stay near the top of the character: both references put the optical axis
    /// just above the head, and pushing it higher walks the character off the
    /// bottom of the frame rather than lowering them within it.
    /// </remarks>
    [System.Serializable]
    public struct CameraFramingProfile
    {
        [Min(0f)]
        public float pivotHeight;

        public float shoulderOffset;

        [Min(0.01f)]
        public float followDistance;

        [Range(1f, 179f)]
        public float fieldOfView;

        public CameraFramingProfile(
            float pivotHeight,
            float shoulderOffset,
            float followDistance,
            float fieldOfView)
        {
            this.pivotHeight = pivotHeight;
            this.shoulderOffset = shoulderOffset;
            this.followDistance = followDistance;
            this.fieldOfView = fieldOfView;
        }

        public static CameraFramingProfile Lerp(
            CameraFramingProfile from,
            CameraFramingProfile to,
            float t)
        {
            return new CameraFramingProfile(
                Mathf.Lerp(from.pivotHeight, to.pivotHeight, t),
                Mathf.Lerp(from.shoulderOffset, to.shoulderOffset, t),
                Mathf.Lerp(from.followDistance, to.followDistance, t),
                Mathf.Lerp(from.fieldOfView, to.fieldOfView, t));
        }

        public CameraFramingProfile Sanitized()
        {
            return new CameraFramingProfile(
                Mathf.Max(0f, pivotHeight),
                shoulderOffset,
                Mathf.Max(0.01f, followDistance),
                Mathf.Clamp(fieldOfView, 1f, 179f));
        }
    }

    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class ThirdPersonFollowCamera : MonoBehaviour
    {
        [Header("Framing")]
        [SerializeField]
        private CameraFramingProfile hipFraming =
            new CameraFramingProfile(2.15f, 0.45f, 3.7f, 65f);

        [SerializeField]
        private CameraFramingProfile aimFraming =
            new CameraFramingProfile(1.95f, 0.65f, 3.4f, 47f);

        /// <summary>
        /// Seconds to cross the whole hip-to-aim blend. Runs on unscaled time so a
        /// hitstop or a paused simulation does not freeze the camera mid-blend.
        /// </summary>
        [SerializeField]
        [Min(0f)]
        private float framingBlendTime = 0.18f;

        [SerializeField]
        [Min(0.01f)]
        private float nearClipPlane = 0.1f;

        [Header("Orbit")]
        [SerializeField]
        private float initialYaw = 0f;

        [SerializeField]
        private float initialPitch = 12f;

        [SerializeField]
        private float yawSpeedDegreesPerSecond = 90f;

        [SerializeField]
        private float pitchSpeedDegreesPerSecond = 60f;

        /// <summary>
        /// Orbit speeds while aimed. Slower than the hip speeds on purpose: the aim
        /// profile narrows the lens, so the same angular rate would sweep the
        /// reticle across far more of the frame and make fine aim impossible.
        /// Blended by <see cref="AimBlend"/> alongside the framing.
        /// </summary>
        [SerializeField]
        private float aimYawSpeedDegreesPerSecond = 55f;

        [SerializeField]
        private float aimPitchSpeedDegreesPerSecond = 35f;

        [Header("Reticle")]
        /// <summary>
        /// Where the reticle sits, as an offset from the middle of the screen in
        /// viewport units (x is a fraction of the full width, y of the height).
        /// </summary>
        /// <remarks>
        /// The aim default comes off the over-the-shoulder reference, whose reticle
        /// sits about 13% of the frame width right of centre and dead on the
        /// vertical middle. That is a deliberate UI placement, not parallax: the
        /// character occupies the left of the frame, so the reticle is pushed the
        /// other way to keep the target clear of them. <see cref="AimDirection"/>
        /// is computed through this same point, so the reticle can never disagree
        /// with where a shot actually goes.
        /// </remarks>
        [SerializeField]
        private Vector2 hipReticleViewportOffset = Vector2.zero;

        [SerializeField]
        private Vector2 aimReticleViewportOffset = new Vector2(0.13f, 0f);

        [SerializeField]
        private float minimumPitch = -10f;

        [SerializeField]
        private float maximumPitch = 55f;

        [Header("Following")]
        [SerializeField]
        [Min(0f)]
        private float positionSmoothTime = 0.1f;

        [Header("Collision (disabled until scene colliders are available)")]
        [SerializeField]
        private bool enableCollision = false;

        [SerializeField]
        private LayerMask collisionMask = ~0;

        [SerializeField]
        [Min(0f)]
        private float collisionRadius = 0.2f;

        [SerializeField]
        [Min(0f)]
        private float minimumCollisionDistance = 0.5f;

        private Camera controlledCamera;
        private InputAction orbitAction;
        private Transform followTarget;
        private Vector3 positionVelocity;
        private float yaw = 0f;
        private float pitch = 12f;
        private bool isAiming;
        private float aimBlend;

        public Transform FollowTarget => followTarget;
        public float CurrentYaw => yaw;
        public float CurrentPitch => pitch;
        public bool IsAiming => isAiming;

        /// <summary>0 while fully hip-framed, 1 while fully aim-framed.</summary>
        public float AimBlend => aimBlend;

        public CameraFramingProfile CurrentFraming =>
            CameraFramingProfile.Lerp(hipFraming, aimFraming, aimBlend);

        /// <summary>
        /// The reticle's position in normalized viewport coordinates, where
        /// (0.5, 0.5) is the middle of the screen.
        /// </summary>
        public Vector2 CurrentReticleViewportPoint
        {
            get
            {
                Vector2 offset = Vector2.Lerp(
                    hipReticleViewportOffset, aimReticleViewportOffset, aimBlend);
                return new Vector2(0.5f + offset.x, 0.5f + offset.y);
            }
        }

        /// <summary>
        /// The world direction a shot should travel to land under the reticle,
        /// which is the ray through <see cref="CurrentReticleViewportPoint"/>
        /// rather than the camera's own forward. With a centred reticle the two
        /// are the same; off centre they are not, and the reticle is the one that
        /// has to be right.
        /// </summary>
        /// <remarks>
        /// This is a direction, not a convergence solution. The shot leaves the
        /// character and the ray leaves the camera, so at close range there is
        /// still parallax between where the reticle sits and where the shot lands.
        /// Closing that needs a hit point to aim the muzzle at, not just an angle.
        /// </remarks>
        public Vector3 AimDirection
        {
            get
            {
                Vector2 point = CurrentReticleViewportPoint;
                if (controlledCamera != null)
                {
                    Vector3 direction = controlledCamera
                        .ViewportPointToRay(new Vector3(point.x, point.y, 0f))
                        .direction;
                    if (direction.sqrMagnitude > 0.000001f)
                    {
                        return direction.normalized;
                    }
                }

                return transform.forward;
            }
        }

        private void Awake()
        {
            controlledCamera = GetComponent<Camera>();
            yaw = initialYaw;
            pitch = Mathf.Clamp(initialPitch, minimumPitch, maximumPitch);
            ApplyCameraSettings();
            EnsureOrbitAction();
        }

        private void OnEnable()
        {
            EnsureOrbitAction();
            orbitAction.Enable();
        }

        private void OnDisable()
        {
            orbitAction?.Disable();
            positionVelocity = Vector3.zero;
        }

        private void OnDestroy()
        {
            orbitAction?.Dispose();
            orbitAction = null;
        }

        private void OnValidate()
        {
            if (maximumPitch < minimumPitch)
            {
                maximumPitch = minimumPitch;
            }

            pitch = Mathf.Clamp(pitch, minimumPitch, maximumPitch);
            hipFraming = hipFraming.Sanitized();
            aimFraming = aimFraming.Sanitized();
            if (controlledCamera == null)
            {
                controlledCamera = GetComponent<Camera>();
            }
            ApplyCameraSettings();
        }

        private void Update()
        {
            Vector2 orbitInput = orbitAction == null
                ? Vector2.zero
                : orbitAction.ReadValue<Vector2>();
            float deltaTime = Time.unscaledDeltaTime;
            ApplyOrbitInput(orbitInput, deltaTime);
            UpdateFraming(deltaTime);
        }

        private void LateUpdate()
        {
            if (followTarget == null)
            {
                return;
            }

            Pose desiredPose = CalculateDesiredPose(followTarget.position);
            float deltaTime = Time.deltaTime;
            transform.position = positionSmoothTime <= 0f || deltaTime <= 0f
                ? desiredPose.position
                : Vector3.SmoothDamp(
                    transform.position,
                    desiredPose.position,
                    ref positionVelocity,
                    positionSmoothTime,
                    Mathf.Infinity,
                    deltaTime);
            transform.rotation = desiredPose.rotation;
        }

        public void SetTarget(Transform target)
        {
            if (followTarget == target)
            {
                return;
            }

            followTarget = target;
            positionVelocity = Vector3.zero;
            if (followTarget != null)
            {
                SnapToTarget();
            }
        }

        /// <summary>
        /// Requests the aim or hip framing. The change is a blend, not a cut, so
        /// callers may drive this every frame from a held button without having to
        /// debounce it.
        /// </summary>
        public void SetAiming(bool aiming)
        {
            isAiming = aiming;
        }

        /// <summary>
        /// Advances the framing blend and pushes the result at the camera. Split
        /// out of <see cref="Update"/> so the blend can be stepped deterministically
        /// from a test without a running player loop.
        /// </summary>
        public void UpdateFraming(float deltaTime)
        {
            float goal = isAiming ? 1f : 0f;
            aimBlend = framingBlendTime <= 0f || deltaTime <= 0f
                ? goal
                : Mathf.MoveTowards(aimBlend, goal, deltaTime / framingBlendTime);
            ApplyCameraSettings();
        }

        /// <summary>
        /// Drops the framing blend straight onto whichever profile is currently
        /// requested, skipping the transition.
        /// </summary>
        public void SnapFraming()
        {
            aimBlend = isAiming ? 1f : 0f;
            ApplyCameraSettings();
            if (followTarget != null)
            {
                SnapToTarget();
            }
        }

        public void ApplyOrbitInput(Vector2 orbitInput, float deltaTime)
        {
            if (deltaTime <= 0f)
            {
                return;
            }

            float yawSpeed = Mathf.Lerp(
                yawSpeedDegreesPerSecond, aimYawSpeedDegreesPerSecond, aimBlend);
            float pitchSpeed = Mathf.Lerp(
                pitchSpeedDegreesPerSecond, aimPitchSpeedDegreesPerSecond, aimBlend);
            yaw += orbitInput.x * yawSpeed * deltaTime;
            pitch = Mathf.Clamp(
                pitch - orbitInput.y * pitchSpeed * deltaTime,
                minimumPitch,
                maximumPitch);
        }

        public void SnapToTarget()
        {
            if (followTarget == null)
            {
                return;
            }

            Pose desiredPose = CalculateDesiredPose(followTarget.position);
            transform.SetPositionAndRotation(desiredPose.position, desiredPose.rotation);
            positionVelocity = Vector3.zero;
        }

        public Pose CalculateDesiredPose(Vector3 targetPosition)
        {
            CameraFramingProfile framing = CurrentFraming;
            Quaternion yawRotation = Quaternion.Euler(0f, yaw, 0f);
            Quaternion orbitRotation = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 pivot = targetPosition + Vector3.up * framing.pivotHeight;
            Vector3 shoulderPivot =
                pivot + yawRotation * Vector3.right * framing.shoulderOffset;
            Vector3 desiredPosition =
                shoulderPivot - orbitRotation * Vector3.forward * framing.followDistance;

            if (enableCollision)
            {
                desiredPosition = ResolveCollision(shoulderPivot, desiredPosition);
            }

            return new Pose(desiredPosition, orbitRotation);
        }

        private Vector3 ResolveCollision(Vector3 pivot, Vector3 desiredPosition)
        {
            Vector3 offset = desiredPosition - pivot;
            float desiredDistance = offset.magnitude;
            if (desiredDistance <= 0.0001f)
            {
                return desiredPosition;
            }

            Vector3 direction = offset / desiredDistance;
            if (!Physics.SphereCast(
                    pivot,
                    collisionRadius,
                    direction,
                    out RaycastHit hit,
                    desiredDistance,
                    collisionMask,
                    QueryTriggerInteraction.Ignore))
            {
                return desiredPosition;
            }

            float resolvedDistance = Mathf.Clamp(
                hit.distance,
                minimumCollisionDistance,
                desiredDistance);
            return pivot + direction * resolvedDistance;
        }

        private void ApplyCameraSettings()
        {
            if (controlledCamera == null)
            {
                return;
            }

            controlledCamera.fieldOfView = CurrentFraming.fieldOfView;
            controlledCamera.nearClipPlane = nearClipPlane;
            controlledCamera.clearFlags = CameraClearFlags.Skybox;
        }

        private void EnsureOrbitAction()
        {
            if (orbitAction != null)
            {
                return;
            }

            orbitAction = new InputAction(
                "Camera Orbit",
                InputActionType.Value,
                expectedControlType: "Vector2");
            orbitAction.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/upArrow")
                .With("Down", "<Keyboard>/downArrow")
                .With("Left", "<Keyboard>/leftArrow")
                .With("Right", "<Keyboard>/rightArrow");
            // Q/E yaw the camera without leaving WASD. Yaw only, so there is no
            // pitch binding on this composite.
            orbitAction.AddCompositeBinding("2DVector")
                .With("Left", "<Keyboard>/q")
                .With("Right", "<Keyboard>/e");
            orbitAction.AddBinding("<Gamepad>/rightStick");
        }
    }
}
