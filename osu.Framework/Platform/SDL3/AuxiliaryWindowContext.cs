// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Input;
using osu.Framework.Platform;
using osuTK;
using osuTK.Input;

namespace osu.Framework.Platform.SDL3
{
    internal sealed class AuxiliaryWindowContext : IDisposable, IAuxiliaryPresentationWindow
    {
        private readonly GameHost host;
        private readonly SDL3Window window;
        private readonly ISDLWindow inputWindow;
        private Vector2I? lastRequestedLogicalSize;
        private Vector2I? lastRequestedFixedSize;
        private int isClosing;
        private int isDisposed;

        public IGraphicsSurface GraphicsSurface => ((IWindow)window).GraphicsSurface;

        public Vector2I ClientSize
        {
            get
            {
                System.Drawing.Size size = window.ClientSize;
                return new Vector2I(size.Width, size.Height);
            }
        }

        public Vector2I LogicalSize
        {
            get
            {
                System.Drawing.Size size = window.Size;
                return new Vector2I(size.Width, size.Height);
            }
        }

        public bool IsMouseInside => window.CursorInWindow.Value;
        public bool IsClosing => Volatile.Read(ref isClosing) != 0;

        public event Action<Vector2>? MouseMoved;
        public event Action<MouseButton, bool>? MouseButtonChanged;
        public event Action<Key, bool>? KeyChanged;
        public event Action<Vector2, bool>? MouseWheel;
        public event Action<string>? TextInput;
        public event Action<string, int, int>? ImeComposition;
        public event Action<bool>? FocusChanged;
        public event Action<Vector2I>? LogicalSizeChanged;
        public event Action? CloseRequested;

        public AuxiliaryWindowContext(GameHost host, SDL3Window window)
        {
            this.host = host;
            this.window = window;
            inputWindow = window;
            lastRequestedLogicalSize = LogicalSize;

            inputWindow.MouseMove += onMouseMoved;
            inputWindow.MouseDown += onMouseDown;
            inputWindow.MouseUp += onMouseUp;
            inputWindow.KeyDown += onKeyDown;
            inputWindow.KeyUp += onKeyUp;
            inputWindow.MouseWheel += onMouseWheel;
            inputWindow.TextInput += onTextInput;
            inputWindow.TextEditing += onImeComposition;
            inputWindow.IsActive.ValueChanged += onFocusChanged;
            window.Resized += onLogicalSizeChanged;
            window.ExitRequested += onCloseRequested;
        }

        public void UpdateLogicalSize(Vector2I size, Vector2I fixedSize, bool forceResize = false)
        {
            if (size.X <= 0 || size.Y <= 0 || fixedSize.X < 0 || fixedSize.Y < 0)
                return;

            bool updateSize = forceResize || lastRequestedLogicalSize != size;
            bool updateConstraints = lastRequestedFixedSize != fixedSize;

            if (!updateSize && !updateConstraints)
                return;

            window.SetWindowSizeAndConstraints(
                new System.Drawing.Size(size.X, size.Y),
                new System.Drawing.Size(fixedSize.X, fixedSize.Y),
                updateSize,
                updateConstraints);
            lastRequestedLogicalSize = size;
            lastRequestedFixedSize = fixedSize;
        }

        public void ObserveLogicalSize(Vector2I size, bool acceptAsRequested)
        {
            if (acceptAsRequested && size.X > 0 && size.Y > 0)
                lastRequestedLogicalSize = size;
        }

        public void BeginClose() => Interlocked.Exchange(ref isClosing, 1);

        public void StartTextInput(TextInputProperties properties) => inputWindow.StartTextInput(properties);

        public void StopTextInput() => inputWindow.StopTextInput();

        public void SetImeRectangle(RectangleF rectangle) => inputWindow.SetTextInputRect(rectangle);

        public void ResetIme() => inputWindow.ResetIme();

        private void onMouseMoved(Vector2 position) => MouseMoved?.Invoke(position);

        private void onMouseDown(MouseButton button) => MouseButtonChanged?.Invoke(button, true);

        private void onMouseUp(MouseButton button) => MouseButtonChanged?.Invoke(button, false);

        private void onKeyDown(Key key) => KeyChanged?.Invoke(key, true);

        private void onKeyUp(Key key) => KeyChanged?.Invoke(key, false);

        private void onMouseWheel(Vector2 delta, bool precise) => MouseWheel?.Invoke(delta, precise);

        private void onTextInput(string text)
            => host.InputThread.Scheduler.Add(() => TextInput?.Invoke(text), false);

        private void onImeComposition(string text, int start, int length)
            => host.InputThread.Scheduler.Add(() => ImeComposition?.Invoke(text, start, length), false);

        private void onFocusChanged(ValueChangedEvent<bool> value)
            => FocusChanged?.Invoke(value.NewValue);

        private void onLogicalSizeChanged()
        {
            Vector2I size = LogicalSize;
            LogicalSizeChanged?.Invoke(size);
        }

        private void onCloseRequested()
        {
            if (Interlocked.Exchange(ref isClosing, 1) == 0)
                CloseRequested?.Invoke();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref isDisposed, 1) != 0)
                return;

            Interlocked.Exchange(ref isClosing, 1);

            inputWindow.MouseMove -= onMouseMoved;
            inputWindow.MouseDown -= onMouseDown;
            inputWindow.MouseUp -= onMouseUp;
            inputWindow.KeyDown -= onKeyDown;
            inputWindow.KeyUp -= onKeyUp;
            inputWindow.MouseWheel -= onMouseWheel;
            inputWindow.TextInput -= onTextInput;
            inputWindow.TextEditing -= onImeComposition;
            inputWindow.IsActive.ValueChanged -= onFocusChanged;
            window.Resized -= onLogicalSizeChanged;
            window.ExitRequested -= onCloseRequested;

            window.Dispose();
        }
    }
}
