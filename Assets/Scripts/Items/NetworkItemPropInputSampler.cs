using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NetworkExample.UnityDemo.Items
{
    [Flags]
    public enum ItemPropInputCommand
    {
        None = 0,
        Throw = 1 << 0,
        Pickup = 1 << 1,
        Use = 1 << 2,
        SelectNextItem = 1 << 3,
        Interact = 1 << 4,
    }

    [DisallowMultipleComponent]
    public sealed class NetworkItemPropInputSampler : MonoBehaviour
    {
        private InputAction throwAction;
        private InputAction pickupAction;
        private InputAction useAction;
        private InputAction selectNextItemAction;
        private InputAction interactAction;
        private bool wasThrowPressed;
        private bool wasPickupPressed;
        private bool wasUsePressed;
        private bool wasSelectNextItemPressed;
        private bool wasInteractPressed;

        private void Awake()
        {
            EnsureActionsCreated();
        }

        private void EnsureActionsCreated()
        {
            if (throwAction != null)
            {
                return;
            }

            throwAction = CreateButtonAction("ThrowItem", "<Keyboard>/r");
            pickupAction = CreateButtonAction("PickupItem", "<Keyboard>/t");
            useAction = CreateButtonAction("UseItem", "<Keyboard>/y");
            selectNextItemAction = CreateButtonAction(
                "SelectNextItem",
                "<Keyboard>/tab");
            // Going into a building and coming back out. E is taken by the
            // camera's orbit.
            interactAction = CreateButtonAction("Interact", "<Keyboard>/f");
        }

        private void OnEnable()
        {
            throwAction?.Enable();
            pickupAction?.Enable();
            useAction?.Enable();
            selectNextItemAction?.Enable();
            interactAction?.Enable();
        }

        private void OnDisable()
        {
            throwAction?.Disable();
            pickupAction?.Disable();
            useAction?.Disable();
            selectNextItemAction?.Disable();
            interactAction?.Disable();
            ResetSession();
        }

        private void OnDestroy()
        {
            DisposeAction(ref throwAction);
            DisposeAction(ref pickupAction);
            DisposeAction(ref useAction);
            DisposeAction(ref selectNextItemAction);
            DisposeAction(ref interactAction);
        }

        public ItemPropInputCommand SampleCommands()
        {
            EnsureActionsCreated();
            EnsureActionsEnabled();
            ItemPropInputCommand commands = ItemPropInputCommand.None;
            AddPressedCommand(
                throwAction,
                ItemPropInputCommand.Throw,
                ref wasThrowPressed,
                ref commands);
            AddPressedCommand(
                pickupAction,
                ItemPropInputCommand.Pickup,
                ref wasPickupPressed,
                ref commands);
            AddPressedCommand(
                useAction,
                ItemPropInputCommand.Use,
                ref wasUsePressed,
                ref commands);
            AddPressedCommand(
                selectNextItemAction,
                ItemPropInputCommand.SelectNextItem,
                ref wasSelectNextItemPressed,
                ref commands);
            AddPressedCommand(
                interactAction,
                ItemPropInputCommand.Interact,
                ref wasInteractPressed,
                ref commands);
            return commands;
        }

        public void ResetSession()
        {
            wasThrowPressed = false;
            wasPickupPressed = false;
            wasUsePressed = false;
            wasSelectNextItemPressed = false;
            wasInteractPressed = false;
        }

        private static InputAction CreateButtonAction(string name, string binding)
        {
            var action = new InputAction(name, InputActionType.Button);
            action.AddBinding(binding);
            return action;
        }

        private void EnsureActionsEnabled()
        {
            throwAction?.Enable();
            pickupAction?.Enable();
            useAction?.Enable();
            selectNextItemAction?.Enable();
            interactAction?.Enable();
        }

        private static void AddPressedCommand(
            InputAction action,
            ItemPropInputCommand command,
            ref bool wasPressed,
            ref ItemPropInputCommand commands)
        {
            bool isPressed = action != null && action.IsPressed();
            if (isPressed && !wasPressed)
            {
                commands |= command;
            }

            wasPressed = isPressed;
        }

        private static void DisposeAction(ref InputAction action)
        {
            action?.Dispose();
            action = null;
        }
    }
}
