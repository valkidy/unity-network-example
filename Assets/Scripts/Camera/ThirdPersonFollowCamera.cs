using UnityEngine;
using UnityEngine.InputSystem;

namespace NetworkExample.UnityDemo.CameraSystem
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class ThirdPersonFollowCamera : MonoBehaviour
    {
        [Header("Framing")]
        [SerializeField]
        [Min(0f)]
        private float pivotHeight = 1.45f;

        [SerializeField]
        private float shoulderOffset = 0.6f;

        [SerializeField]
        [Min(0.01f)]
        private float followDistance = 4.5f;

        [SerializeField]
        [Range(1f, 179f)]
        private float fieldOfView = 65f;

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

        public Transform FollowTarget => followTarget;
        public float CurrentYaw => yaw;
        public float CurrentPitch => pitch;

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
            ApplyOrbitInput(orbitInput, Time.unscaledDeltaTime);
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

        public void ApplyOrbitInput(Vector2 orbitInput, float deltaTime)
        {
            if (deltaTime <= 0f)
            {
                return;
            }

            yaw += orbitInput.x * yawSpeedDegreesPerSecond * deltaTime;
            pitch = Mathf.Clamp(
                pitch - orbitInput.y * pitchSpeedDegreesPerSecond * deltaTime,
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
            Quaternion yawRotation = Quaternion.Euler(0f, yaw, 0f);
            Quaternion orbitRotation = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 pivot = targetPosition + Vector3.up * pivotHeight;
            Vector3 shoulderPivot =
                pivot + yawRotation * Vector3.right * shoulderOffset;
            Vector3 desiredPosition =
                shoulderPivot - orbitRotation * Vector3.forward * followDistance;

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

            controlledCamera.fieldOfView = fieldOfView;
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
            orbitAction.AddBinding("<Gamepad>/rightStick");
        }
    }
}
