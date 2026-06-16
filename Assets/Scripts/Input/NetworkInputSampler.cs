using NetworkExample.Kernel;
using UnityEngine;
using UnityEngine.InputSystem;
using KernelPlayerInput = NetworkExample.Kernel.PlayerInput;

namespace NetworkExample.UnityDemo.Input
{
    [DisallowMultipleComponent]
    public sealed class NetworkInputSampler : MonoBehaviour
    {
        private static readonly string[] WeaponSelectBindings =
        {
            "<Keyboard>/1",
            "<Keyboard>/2",
            "<Keyboard>/3",
            "<Keyboard>/4",
            "<Keyboard>/5",
            "<Keyboard>/6",
            "<Keyboard>/7",
        };

        [SerializeField]
        private float firePressedThreshold = 0.5f;

        private InputAction moveAction;
        private InputAction fireAction;
        private InputAction[] weaponSelectActions;
        private InputAction reloadAction;
        private uint inputSequence;
        private uint nextClientActionId = 1;
        private byte selectedWeapon;
        private bool wasFirePressed;

        private void Awake()
        {
            moveAction = new InputAction("Move", InputActionType.Value, expectedControlType: "Vector2");
            moveAction.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w")
                .With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a")
                .With("Right", "<Keyboard>/d");
            moveAction.AddBinding("<Gamepad>/leftStick");

            fireAction = new InputAction("Fire", InputActionType.Button);
            // fireAction.AddBinding("<Mouse>/leftButton");
            fireAction.AddBinding("<Keyboard>/space");
            fireAction.AddBinding("<Gamepad>/rightTrigger");

            weaponSelectActions = new InputAction[KernelConstants.MaxWeapons];
            for (int index = 0; index < weaponSelectActions.Length; ++index)
            {
                InputAction selectAction = new InputAction(
                    "SelectWeapon" + index,
                    InputActionType.Button);
                if (index < WeaponSelectBindings.Length)
                {
                    selectAction.AddBinding(WeaponSelectBindings[index]);
                }

                weaponSelectActions[index] = selectAction;
            }

            reloadAction = new InputAction("Reload", InputActionType.Button);
            reloadAction.AddBinding("<Keyboard>/r");
        }

        private void OnEnable()
        {
            moveAction?.Enable();
            fireAction?.Enable();
            SetWeaponSelectActionsEnabled(true);
            reloadAction?.Enable();
        }

        private void OnDisable()
        {
            moveAction?.Disable();
            fireAction?.Disable();
            SetWeaponSelectActionsEnabled(false);
            reloadAction?.Disable();
            wasFirePressed = false;
        }

        private void OnDestroy()
        {
            moveAction?.Dispose();
            moveAction = null;
            fireAction?.Dispose();
            fireAction = null;
            if (weaponSelectActions != null)
            {
                for (int index = 0; index < weaponSelectActions.Length; ++index)
                {
                    weaponSelectActions[index]?.Dispose();
                    weaponSelectActions[index] = null;
                }
            }
            weaponSelectActions = null;
            reloadAction?.Dispose();
            reloadAction = null;
        }

        public KernelPlayerInput Sample()
        {
            Vector2 move = moveAction == null ? Vector2.zero : moveAction.ReadValue<Vector2>();
            move = Vector2.ClampMagnitude(move, 1f);

            UpdateSelectedWeapon();
            bool isFirePressed = IsFirePressed();
            bool fireTriggered = isFirePressed && !wasFirePressed;
            wasFirePressed = isFirePressed;

            uint buttons = 0U;
            if (fireTriggered)
            {
                buttons |= (uint)InputButton.Fire;
            }
            if (IsActionPressed(reloadAction))
            {
                buttons |= (uint)InputButton.Reload;
            }

            Vector3 aimDirection = move.sqrMagnitude > 0.0001f
                ? new Vector3(move.x, 0f, move.y).normalized
                : Vector3.forward;

            inputSequence++;
            uint clientActionId = 0;
            if ((buttons & (uint)InputButton.Fire) != 0)
            {
                clientActionId = nextClientActionId++;
                if (nextClientActionId == 0)
                {
                    nextClientActionId = 1;
                }
            }

            return new KernelPlayerInput
            {
                input_seq = inputSequence,
                client_action_time_us = NowMicroseconds(),
                client_action_id = clientActionId,
                move = new KernelVec2(move.x * 0.1f, move.y * 0.1f), // Scale down movement input for better control at lower tick rates
                look_delta = new KernelVec2(0f, 0f),
                aim_dir = new KernelVec3(aimDirection.x, aimDirection.y, aimDirection.z),
                buttons = buttons,
                selected_weapon = selectedWeapon,
            };
        }

        private bool IsFirePressed()
        {
            return IsActionPressed(fireAction);
        }

        private void UpdateSelectedWeapon()
        {
            if (weaponSelectActions == null)
            {
                return;
            }

            for (int index = 0; index < weaponSelectActions.Length; ++index)
            {
                if (IsActionPressed(weaponSelectActions[index]))
                {
                    selectedWeapon = (byte)index;
                    return;
                }
            }
        }

        private bool IsActionPressed(InputAction action)
        {
            if (action == null)
            {
                return false;
            }

            return action.IsPressed() || action.ReadValue<float>() >= firePressedThreshold;
        }

        private void SetWeaponSelectActionsEnabled(bool enabled)
        {
            if (weaponSelectActions == null)
            {
                return;
            }

            for (int index = 0; index < weaponSelectActions.Length; ++index)
            {
                if (enabled)
                {
                    weaponSelectActions[index]?.Enable();
                }
                else
                {
                    weaponSelectActions[index]?.Disable();
                }
            }
        }

        private static ulong NowMicroseconds()
        {
            return (ulong)(Time.realtimeSinceStartupAsDouble * 1000000.0);
        }
    }
}
