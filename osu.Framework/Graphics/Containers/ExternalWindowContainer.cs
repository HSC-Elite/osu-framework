// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Threading;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Visualisation;
using osu.Framework.Input;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Input.Handlers;
using osu.Framework.Platform;
using osu.Framework.Platform.SDL3;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Graphics.Containers
{
    public partial class ExternalWindowContainer : BufferedContainer<Drawable>
    {
        [Cached(typeof(TextInputSource))]
        private readonly AuxiliaryWindowTextInputSource textInputSource = new AuxiliaryWindowTextInputSource();

        private readonly AuxiliaryInputManager inputManager;
        private DrawVisualiser? drawVisualiser;

        private GameHost host = null!;
        private SDL3Window primaryWindow = null!;
        private IAuxiliaryPresentationRenderer presentationRenderer = null!;
        private AuxiliaryWindowContext? windowContext;
        private CompositeDrawable? subscribedParent;
        private Vector2 clientSize = new Vector2(800, 600);
        private int windowCreationPending;
        private int closing;

        protected override bool ComputeIsMaskedAway(RectangleF maskingBounds) => false;

        internal override bool DrawToMainBackbuffer => false;

        internal override void OnFrameBufferDrawn(IFrameBuffer frameBuffer)
            => presentationRenderer.SetAuxiliaryPresentationSource(frameBuffer);

        protected override Container<Drawable> Content => inputManager;

        public string WindowTitle { get; set; } = "External Window";

        public Vector2 ClientSize
        {
            get => clientSize;
            set => setClientSize(value, true);
        }

        public ExternalWindowContainer()
            : base(clipToRootNode: false)
        {
            inputManager = new AuxiliaryInputManager(toggleExternalDrawVisualiser);
            InternalChild = inputManager;

            Size = clientSize;

            AlwaysPresent = true;
        }

        [BackgroundDependencyLoader]
        private void load(GameHost gameHost)
        {
            if (!osu.Framework.FrameworkEnvironment.UseSDL3 ||
                gameHost.Window is not SDL3Window sdlWindow ||
                gameHost.Renderer is not IAuxiliaryPresentationRenderer auxiliaryPresentationRenderer ||
                !auxiliaryPresentationRenderer.SupportsAuxiliarySurface(((IWindow)sdlWindow).GraphicsSurface))
            {
                throw new NotSupportedException("External windows require SDL3 and a renderer that supports auxiliary presentation.");
            }

            host = gameHost;
            primaryWindow = sdlWindow;
            presentationRenderer = auxiliaryPresentationRenderer;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            subscribedParent = Parent;
            if (subscribedParent != null)
            {
                subscribedParent.ChildDied += onParentChildDied;
                subscribedParent.ChildBecameAlive += onParentChildBecameAlive;
            }

            createAuxiliaryWindow();
        }

        private void createAuxiliaryWindow()
        {
            if (Volatile.Read(ref closing) != 0 ||
                Volatile.Read(ref windowContext) != null ||
                Interlocked.CompareExchange(ref windowCreationPending, 1, 0) != 0)
            {
                return;
            }

            var size = new System.Drawing.Size(
                Math.Max(1, (int)MathF.Ceiling(clientSize.X)),
                Math.Max(1, (int)MathF.Ceiling(clientSize.Y)));

            primaryWindow.CreateSiblingWindow(WindowTitle, size, nativeWindow =>
            {
                Interlocked.Exchange(ref windowCreationPending, 0);

                if (Volatile.Read(ref closing) != 0 || !IsAlive)
                {
                    nativeWindow.Dispose();
                    return;
                }

                nativeWindow.Show();
                var context = new AuxiliaryWindowContext(host, nativeWindow);

                try
                {
                    presentationRenderer.RegisterAuxiliaryWindow(context);
                }
                catch
                {
                    context.Dispose();
                    throw;
                }

                Scheduler.Add(() => attachAuxiliaryWindow(context), false);
            });
        }

        private void attachAuxiliaryWindow(AuxiliaryWindowContext context)
        {
            if (Volatile.Read(ref closing) != 0 ||
                !IsAlive ||
                context.IsClosing ||
                Interlocked.CompareExchange(ref windowContext, context, null) != null)
            {
                presentationRenderer.UnregisterAuxiliaryWindow(context, context.Dispose);
                return;
            }

            inputManager.SetWindow(context);
            textInputSource.Attach(context, rectangle =>
                ToLocalSpace(new Quad(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height)).AABBFloat);
            context.ClientSizeChanged += onNativeClientSizeChanged;
            context.CloseRequested += onWindowCloseRequested;
        }

        private void onNativeClientSizeChanged(Vector2I newSize)
            => Scheduler.Add(() => setClientSize(new Vector2(newSize.X, newSize.Y), false), false);

        private void setClientSize(Vector2 newSize, bool resizeWindow)
        {
            if (newSize.X <= 0 || newSize.Y <= 0 || clientSize == newSize)
                return;

            clientSize = newSize;
            Size = newSize;

            AuxiliaryWindowContext? context = Volatile.Read(ref windowContext);

            if (resizeWindow && context != null)
            {
                context.Resize(new Vector2I(
                    Math.Max(1, (int)MathF.Ceiling(newSize.X)),
                    Math.Max(1, (int)MathF.Ceiling(newSize.Y))));
            }
        }

        private void onWindowCloseRequested()
            => Scheduler.Add(Expire, false);

        private void onParentChildDied(Drawable child)
        {
            if (child == this)
                releaseAuxiliaryWindow();
        }

        private void onParentChildBecameAlive(Drawable child)
        {
            if (child == this && Volatile.Read(ref windowContext) == null && Volatile.Read(ref closing) == 0)
                Scheduler.Add(createAuxiliaryWindow, false);
        }

        private void releaseAuxiliaryWindow()
        {
            AuxiliaryWindowContext? context = Interlocked.Exchange(ref windowContext, null);
            if (context == null)
                return;
            context.ClientSizeChanged -= onNativeClientSizeChanged;
            context.CloseRequested -= onWindowCloseRequested;
            context.BeginClose();

            inputManager.SetWindow(null);
            textInputSource.Detach();
            presentationRenderer.UnregisterAuxiliaryWindow(context, context.Dispose);
        }

        protected override void Dispose(bool isDisposing)
        {
            Interlocked.Exchange(ref closing, 1);
            releaseAuxiliaryWindow();

            if (subscribedParent != null)
            {
                subscribedParent.ChildDied -= onParentChildDied;
                subscribedParent.ChildBecameAlive -= onParentChildBecameAlive;
                subscribedParent = null;
            }

            base.Dispose(isDisposing);
        }

        private enum ExternalWindowFrameworkAction
        {
            ToggleDrawVisualiser
        }

        private void toggleExternalDrawVisualiser()
        {
            if (drawVisualiser == null)
            {
                inputManager.LoadDrawVisualiser(drawVisualiser = new AuxiliaryDrawVisualiser
                {
                    State = { Value = Visibility.Visible },
                    ToolPosition = new Vector2(20),
                });
            }
            else
            {
                drawVisualiser.ToggleVisibility();

                if (drawVisualiser.State.Value == Visibility.Visible)
                    inputManager.ChangeOverlayDepth(drawVisualiser, float.MinValue);
            }
        }

        private sealed partial class AuxiliaryInputManager : CustomInputManager
        {
            private readonly Container content = new Container { RelativeSizeAxes = Axes.Both };
            private AuxiliaryWindowContext? window;
            private AuxiliaryWindowInputHandler? handler;

            protected override Container<Drawable> Content => content;

            protected override bool ComputeIsMaskedAway(RectangleF maskingBounds) => false;

            protected override RectangleF ComputeChildMaskingBounds() => ScreenSpaceDrawQuad.AABBFloat;

            public override bool HandleHoverEvents => Volatile.Read(ref window)?.IsMouseInside == true;

            public AuxiliaryInputManager(Action toggleDrawVisualiser)
            {
                AddInternal(new PlatformActionContainer
                {
                    Child = new AuxiliaryWindowFrameworkActionContainer(toggleDrawVisualiser)
                    {
                        Child = content,
                    },
                });
            }

            public void LoadDrawVisualiser(DrawVisualiser visualiser)
            {
                LoadComponentAsync(visualiser, loadedVisualiser =>
                {
                    AddInternal(loadedVisualiser);
                    ChangeOverlayDepth(loadedVisualiser, float.MinValue);
                });
            }

            public void ChangeOverlayDepth(Drawable overlay, float depth)
                => ChangeInternalChildDepth(overlay, depth);

            public void SetWindow(AuxiliaryWindowContext? newWindow)
            {
                if (Volatile.Read(ref window) == newWindow)
                    return;

                if (handler != null)
                {
                    RemoveHandler(handler);
                    handler.Dispose();
                    handler = null;
                }

                Volatile.Write(ref window, newWindow);

                if (IsLoaded)
                    addInputHandler();
            }

            protected override void LoadComplete()
            {
                base.LoadComplete();
                addInputHandler();
            }

            private void addInputHandler()
            {
                AuxiliaryWindowContext? currentWindow = Volatile.Read(ref window);

                if (currentWindow == null || handler != null)
                    return;

                handler = new AuxiliaryWindowInputHandler(currentWindow, ToScreenSpace);
                AddHandler(handler);
            }

            private sealed partial class AuxiliaryWindowFrameworkActionContainer
                : KeyBindingContainer<ExternalWindowFrameworkAction>, IKeyBindingHandler<ExternalWindowFrameworkAction>
            {
                private readonly Action toggleDrawVisualiser;

                public override IEnumerable<IKeyBinding> DefaultKeyBindings => new[]
                {
                    new KeyBinding(new[] { InputKey.Control, InputKey.F1 }, ExternalWindowFrameworkAction.ToggleDrawVisualiser)
                };

                public AuxiliaryWindowFrameworkActionContainer(Action toggleDrawVisualiser)
                    : base(matchingMode: KeyCombinationMatchingMode.Exact)
                {
                    this.toggleDrawVisualiser = toggleDrawVisualiser;
                }

                public bool OnPressed(KeyBindingPressEvent<ExternalWindowFrameworkAction> e)
                {
                    if (e.Repeat || e.Action != ExternalWindowFrameworkAction.ToggleDrawVisualiser)
                        return false;

                    toggleDrawVisualiser();
                    return true;
                }

                public void OnReleased(KeyBindingReleaseEvent<ExternalWindowFrameworkAction> e)
                {
                }

                protected override bool Prioritised => true;
            }
        }

        [DrawVisualiserHidden]
        private sealed partial class AuxiliaryDrawVisualiser : DrawVisualiser
        {
        }

        private sealed class AuxiliaryWindowTextInputSource : TextInputSource
        {
            private AuxiliaryWindowContext? window;
            private Func<RectangleF, RectangleF> mapImeRectangle = static rectangle => rectangle;

            public void Attach(AuxiliaryWindowContext newWindow, Func<RectangleF, RectangleF> mapImeRectangle)
            {
                Detach();
                window = newWindow;
                this.mapImeRectangle = mapImeRectangle;
                window.TextInput += onTextInput;
                window.ImeComposition += onImeComposition;
            }

            public void Detach()
            {
                if (window == null)
                    return;

                window.TextInput -= onTextInput;
                window.ImeComposition -= onImeComposition;
                window = null;
                mapImeRectangle = static rectangle => rectangle;
            }

            public override void SetImeRectangle(RectangleF rectangle)
                => window?.SetImeRectangle(mapImeRectangle(rectangle));

            protected override void ActivateTextInput(TextInputProperties properties)
                => window?.StartTextInput(properties);

            protected override void EnsureTextInputActivated(TextInputProperties properties)
                => window?.StartTextInput(properties);

            protected override void DeactivateTextInput()
                => window?.StopTextInput();

            public override void ResetIme()
            {
                base.ResetIme();
                window?.ResetIme();
            }

            private void onTextInput(string text)
            {
                if (ImeActive)
                    TriggerImeResult(text);
                else
                    TriggerTextInput(text);
            }

            private void onImeComposition(string text, int start, int length)
                => TriggerImeComposition(text, start, length);

        }
    }
}
