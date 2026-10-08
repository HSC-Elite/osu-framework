// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Input.StateChanges;
using osu.Framework.Input.States;
using osu.Framework.Platform;
using osu.Framework.Platform.SDL3;
using osuTK;
using osuTK.Input;

namespace osu.Framework.Input.Handlers
{
    internal sealed class AuxiliaryWindowInputHandler : InputHandler
    {
        private readonly AuxiliaryWindowContext window;
        private readonly Func<Vector2, Vector2> toScreenSpace;
        private readonly HashSet<Key> pressedKeys = new HashSet<Key>();
        private readonly HashSet<MouseButton> pressedMouseButtons = new HashSet<MouseButton>();

        public override bool IsActive => true;

        public AuxiliaryWindowInputHandler(AuxiliaryWindowContext window, Func<Vector2, Vector2> toScreenSpace)
        {
            this.window = window;
            this.toScreenSpace = toScreenSpace;
        }

        public override bool Initialize(GameHost host)
        {
            if (!base.Initialize(host))
                return false;

            window.MouseMoved += onMouseMoved;
            window.MouseButtonChanged += onMouseButtonChanged;
            window.KeyChanged += onKeyChanged;
            window.MouseWheel += onMouseWheel;
            window.FocusChanged += onFocusChanged;
            return true;
        }

        private void onMouseMoved(Vector2 position)
            => PendingInputs.Enqueue(new AuxiliaryMousePositionInput(position, toScreenSpace));

        private void onMouseButtonChanged(MouseButton button, bool pressed)
        {
            if (pressed)
            {
                if (pressedMouseButtons.Add(button))
                    PendingInputs.Enqueue(new MouseButtonInput(button, true));
            }
            else if (pressedMouseButtons.Remove(button))
                PendingInputs.Enqueue(new MouseButtonInput(button, false));
        }

        private void onKeyChanged(Key key, bool pressed)
        {
            if (pressed)
            {
                if (pressedKeys.Add(key))
                    PendingInputs.Enqueue(new KeyboardKeyInput(key, true));
            }
            else if (pressedKeys.Remove(key))
                PendingInputs.Enqueue(new KeyboardKeyInput(key, false));
        }

        private void onMouseWheel(Vector2 delta, bool precise)
            => PendingInputs.Enqueue(new MouseScrollRelativeInput { Delta = delta, IsPrecise = precise });

        private void onFocusChanged(bool focused)
        {
            if (focused)
                return;

            foreach (Key key in pressedKeys)
                PendingInputs.Enqueue(new KeyboardKeyInput(key, false));

            foreach (MouseButton button in pressedMouseButtons)
                PendingInputs.Enqueue(new MouseButtonInput(button, false));

            pressedKeys.Clear();
            pressedMouseButtons.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            window.MouseMoved -= onMouseMoved;
            window.MouseButtonChanged -= onMouseButtonChanged;
            window.KeyChanged -= onKeyChanged;
            window.MouseWheel -= onMouseWheel;
            window.FocusChanged -= onFocusChanged;

            base.Dispose(disposing);
        }

        private sealed class AuxiliaryMousePositionInput : IInput
        {
            private readonly Vector2 position;
            private readonly Func<Vector2, Vector2> toScreenSpace;

            public AuxiliaryMousePositionInput(Vector2 position, Func<Vector2, Vector2> toScreenSpace)
            {
                this.position = position;
                this.toScreenSpace = toScreenSpace;
            }

            public void Apply(InputState state, IInputStateChangeHandler handler)
            {
                new MousePositionAbsoluteInput
                {
                    Position = toScreenSpace(position)
                }.Apply(state, handler);
            }
        }
    }
}
