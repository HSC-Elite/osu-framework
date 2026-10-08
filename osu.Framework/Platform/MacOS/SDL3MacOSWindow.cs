// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using osu.Framework.Platform.Apple.Native;
using osu.Framework.Platform.SDL3;
using osuTK;
using Selector = osu.Framework.Platform.Apple.Native.Selector;

namespace osu.Framework.Platform.MacOS
{
    /// <summary>
    /// macOS-specific subclass of <see cref="SDL3Window"/>.
    /// </summary>
    internal class SDL3MacOSWindow : SDL3DesktopWindow
    {
        private static readonly object scrollWheelLock = new object();
        private static readonly Dictionary<IntPtr, SDL3MacOSWindow> windowsByNativeHandle = new Dictionary<IntPtr, SDL3MacOSWindow>();

        private static readonly IntPtr sel_window = Selector.Get("window");
        private static readonly IntPtr sel_hasprecisescrollingdeltas = Selector.Get("hasPreciseScrollingDeltas");
        private static readonly IntPtr sel_scrollingdeltax = Selector.Get("scrollingDeltaX");
        private static readonly IntPtr sel_scrollingdeltay = Selector.Get("scrollingDeltaY");
        private static readonly IntPtr sel_respondstoselector_ = Selector.Get("respondsToSelector:");

        private delegate void ScrollWheelDelegate(IntPtr handle, IntPtr selector, IntPtr theEvent); // v@:@

        private static readonly ScrollWheelDelegate scrollWheelHandler = scrollWheel;

        private static IntPtr originalScrollWheel;
        private static bool scrollWheelSwizzled;

        private IntPtr cocoaWindowHandle;

        public SDL3MacOSWindow(GraphicsSurfaceType surfaceType, string appName)
            : base(surfaceType, appName)
        {
        }

        private SDL3MacOSWindow(GraphicsSurfaceType surfaceType, string appName, SDL3WindowRuntime runtime)
            : base(surfaceType, appName, runtime)
        {
        }

        protected override SDL3Window CreateSiblingWindowInstance()
            => new SDL3MacOSWindow(SurfaceType, AppName, Runtime);

        public override void Create()
        {
            base.Create();

            lock (scrollWheelLock)
            {
                if (!scrollWheelSwizzled)
                {
                    IntPtr viewClass = Class.Get("SDL3View");
                    originalScrollWheel = Class.SwizzleMethod(viewClass, "scrollWheel:", "v@:@", scrollWheelHandler);
                    scrollWheelSwizzled = true;
                }

                cocoaWindowHandle = WindowHandle;

                if (cocoaWindowHandle != IntPtr.Zero)
                    windowsByNativeHandle[cocoaWindowHandle] = this;
            }

            Exited += onWindowExited;
        }

        private void onWindowExited()
        {
            Exited -= onWindowExited;

            lock (scrollWheelLock)
            {
                if (windowsByNativeHandle.TryGetValue(cocoaWindowHandle, out SDL3MacOSWindow registeredWindow) && registeredWindow == this)
                    windowsByNativeHandle.Remove(cocoaWindowHandle);

                cocoaWindowHandle = IntPtr.Zero;
            }
        }

        private static void scrollWheel(IntPtr receiver, IntPtr selector, IntPtr theEvent)
        {
            bool hasPrecise = Interop.SendBool(theEvent, sel_respondstoselector_, sel_hasprecisescrollingdeltas) &&
                              Interop.SendBool(theEvent, sel_hasprecisescrollingdeltas);

            if (!hasPrecise)
            {
                invokeOriginalScrollWheel(receiver, theEvent);
                return;
            }

            // according to osuTK, 0.1f is the scaling factor expected to be returned by CGEventSourceGetPixelsPerLine
            // this is additionally scaled down by a factor of 8 so that a precise scroll of 1.0 is roughly equivalent to one notch on a traditional scroll wheel.
            const float scale_factor = 0.1f / 8;

            float scrollingDeltaX = Interop.SendFloat(theEvent, sel_scrollingdeltax);
            float scrollingDeltaY = Interop.SendFloat(theEvent, sel_scrollingdeltay);

            IntPtr cocoaWindowHandle = Interop.SendIntPtr(receiver, sel_window);
            SDL3MacOSWindow window;
            bool hasWindow;

            lock (scrollWheelLock)
                hasWindow = windowsByNativeHandle.TryGetValue(cocoaWindowHandle, out window);

            if (!hasWindow)
            {
                invokeOriginalScrollWheel(receiver, theEvent);
                return;
            }

            window.ScheduleEvent(() => window.TriggerMouseWheel(new Vector2(scrollingDeltaX * scale_factor, scrollingDeltaY * scale_factor), true));
        }

        private static void invokeOriginalScrollWheel(IntPtr receiver, IntPtr theEvent)
        {
            if (originalScrollWheel != IntPtr.Zero && Interop.SendBool(receiver, sel_respondstoselector_, originalScrollWheel))
                Interop.SendVoid(receiver, originalScrollWheel, theEvent);
        }
    }
}
