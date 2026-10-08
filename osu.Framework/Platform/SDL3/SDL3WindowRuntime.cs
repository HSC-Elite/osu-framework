// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using osu.Framework.Allocation;
using osu.Framework.Extensions.EnumExtensions;
using osu.Framework.Logging;
using SDL;
using static SDL.SDL3;

namespace osu.Framework.Platform.SDL3
{
    internal sealed unsafe class SDL3WindowRuntime : IDisposable
    {
        private const int events_per_peep = 64;

        private readonly List<SDL3Window> windows = new List<SDL3Window>();
        private readonly ConcurrentDictionary<SDL_WindowID, SDL3Window> windowsById = new ConcurrentDictionary<SDL_WindowID, SDL3Window>();
        private readonly SDL_Event[] events = new SDL_Event[events_per_peep];
        private readonly ObjectHandle<SDL3WindowRuntime> callbackHandle;

        private SDL3Window? primaryWindow;
        private bool isShutdown;

        public bool IsWayland { get; }

        public SDL3WindowRuntime(string appName)
        {
            callbackHandle = new ObjectHandle<SDL3WindowRuntime>(this, GCHandleType.Normal);

            SDL_SetHint(SDL_HINT_APP_NAME, appName).LogErrorIfFailed();

            if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO | SDL_InitFlags.SDL_INIT_GAMEPAD))
            {
                callbackHandle.Dispose();
                throw new InvalidOperationException($"Failed to initialise SDL: {SDL_GetError()}");
            }

            int version = SDL_GetVersion();
            Logger.Log($@"SDL3 Initialized
                          SDL3 Version: {SDL_VERSIONNUM_MAJOR(version)}.{SDL_VERSIONNUM_MINOR(version)}.{SDL_VERSIONNUM_MICRO(version)}
                          SDL3 Revision: {SDL_GetRevision()}
                          SDL3 Video driver: {SDL_GetCurrentVideoDriver()}");

            IsWayland = SDL_GetCurrentVideoDriver() == "wayland";

            SDL_SetLogOutputFunction(&logOutput, IntPtr.Zero);
            SDL_SetEventFilter(&eventFilter, callbackHandle.Handle);
            SDL_AddEventWatch(&eventWatch, callbackHandle.Handle).LogErrorIfFailed();
        }

        public void SetPrimaryWindow(SDL3Window window)
        {
            if (primaryWindow != null)
                throw new InvalidOperationException("An SDL3 runtime can only have one primary window.");

            primaryWindow = window;
        }

        public void RegisterWindow(SDL3Window window)
        {
            ObjectDisposedException.ThrowIf(isShutdown, this);

            SDL_WindowID windowId = SDL_GetWindowID(window.SDLWindowHandle);

            if (!windowsById.TryAdd(windowId, window))
                throw new InvalidOperationException($"An SDL3 window with ID {windowId} is already registered.");

            windows.Add(window);
        }

        public void UnregisterWindow(SDL3Window window)
        {
            if (window.SDLWindowHandle != null)
                windowsById.TryRemove(SDL_GetWindowID(window.SDLWindowHandle), out _);

            windows.Remove(window);
        }

        public void RunFrame()
        {
            SDL3Window? primary = primaryWindow;

            if (primary == null || isShutdown)
                return;

            primary.RunFrameBeforeEventPump();

            for (int i = 1; i < windows.Count; i++)
                windows[i].RunFrameBeforeEventPump();

            if (!primary.Exists)
                return;

            SDL_PumpEvents();

            int eventsRead;

            do
            {
                eventsRead = SDL_PeepEvents(events, SDL_EventAction.SDL_GETEVENT, SDL_EventType.SDL_EVENT_FIRST, SDL_EventType.SDL_EVENT_LAST).LogErrorIfFailed();

                for (int i = 0; i < eventsRead; i++)
                    dispatchEvent(events[i]);
            } while (eventsRead == events_per_peep);

            for (int i = 0; i < windows.Count; i++)
                windows[i].RunFrameAfterEventPump();

            for (int i = windows.Count - 1; i > 0; i--)
            {
                if (windows[i].Exists)
                    continue;

                SDL3Window closingWindow = windows[i];
                closingWindow.NotifyExited();
                closingWindow.DestroyNativeWindow();
            }
        }

        public void Shutdown()
        {
            if (isShutdown)
                return;

            isShutdown = true;

            SDL3Window[] windowsToClose = windows.ToArray();

            foreach (SDL3Window window in windowsToClose)
            {
                window.NotifyExited();
                window.DestroyNativeWindow();
            }

            SDL_Quit();
            callbackHandle.Dispose();
        }

        private void dispatchEvent(SDL_Event e)
        {
            if (e.Type >= SDL_EventType.SDL_EVENT_DISPLAY_FIRST && e.Type <= SDL_EventType.SDL_EVENT_DISPLAY_LAST)
            {
                for (int i = 0; i < windows.Count; i++)
                    windows[i].DispatchEvent(e);

                return;
            }

            if (tryGetWindowId(e, out SDL_WindowID windowId))
            {
                if (windowsById.TryGetValue(windowId, out SDL3Window? window))
                    window.DispatchEvent(e);

                return;
            }

            primaryWindow?.DispatchEvent(e);
        }

        private bool dispatchEventFromFilter(SDL_Event e)
        {
            SDL3Window? window = getWindowForEvent(e);
            return window?.DispatchEventFromFilter(e) ?? true;
        }

        private void dispatchEventFromWatch(SDL_Event e)
        {
            getWindowForEvent(e)?.DispatchEventFromWatch(e);
        }

        private SDL3Window? getWindowForEvent(SDL_Event e)
        {
            if (tryGetWindowId(e, out SDL_WindowID windowId))
                return windowsById.TryGetValue(windowId, out SDL3Window? window) ? window : null;

            return primaryWindow;
        }

        private static bool tryGetWindowId(SDL_Event e, out SDL_WindowID windowId)
        {
            if (e.Type >= SDL_EventType.SDL_EVENT_WINDOW_FIRST && e.Type <= SDL_EventType.SDL_EVENT_WINDOW_LAST)
            {
                windowId = e.window.windowID;
                return true;
            }

            switch (e.Type)
            {
                case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                    windowId = e.motion.windowID;
                    return true;

                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                    windowId = e.button.windowID;
                    return true;

                case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                    windowId = e.wheel.windowID;
                    return true;

                case SDL_EventType.SDL_EVENT_KEY_DOWN:
                case SDL_EventType.SDL_EVENT_KEY_UP:
                    windowId = e.key.windowID;
                    return true;

                case SDL_EventType.SDL_EVENT_TEXT_EDITING:
                    windowId = e.edit.windowID;
                    return true;

                case SDL_EventType.SDL_EVENT_TEXT_INPUT:
                    windowId = e.text.windowID;
                    return true;

                case SDL_EventType.SDL_EVENT_DROP_FILE:
                case SDL_EventType.SDL_EVENT_DROP_TEXT:
                case SDL_EventType.SDL_EVENT_DROP_BEGIN:
                case SDL_EventType.SDL_EVENT_DROP_COMPLETE:
                    windowId = e.drop.windowID;
                    return true;

                case SDL_EventType.SDL_EVENT_FINGER_DOWN:
                case SDL_EventType.SDL_EVENT_FINGER_UP:
                case SDL_EventType.SDL_EVENT_FINGER_MOTION:
                case SDL_EventType.SDL_EVENT_FINGER_CANCELED:
                    windowId = e.tfinger.windowID;
                    return true;

                case SDL_EventType.SDL_EVENT_PEN_PROXIMITY_IN:
                case SDL_EventType.SDL_EVENT_PEN_PROXIMITY_OUT:
                    windowId = e.pproximity.windowID;
                    return true;

                case SDL_EventType.SDL_EVENT_PEN_DOWN:
                case SDL_EventType.SDL_EVENT_PEN_UP:
                    windowId = e.ptouch.windowID;
                    return true;

                case SDL_EventType.SDL_EVENT_PEN_BUTTON_DOWN:
                case SDL_EventType.SDL_EVENT_PEN_BUTTON_UP:
                    windowId = e.pbutton.windowID;
                    return true;

                case SDL_EventType.SDL_EVENT_PEN_MOTION:
                    windowId = e.pmotion.windowID;
                    return true;

                default:
                    windowId = 0;
                    return false;
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void logOutput(IntPtr _, int category, SDL_LogPriority priority, byte* messagePtr)
        {
            SDL_LogCategory categoryEnum = (SDL_LogCategory)category;
            string? message = PtrToStringUTF8(messagePtr);
            Logger.Log($"SDL {categoryEnum.ReadableName()} log [{priority.ReadableName()}]: {message}");
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static SDLBool eventFilter(IntPtr userdata, SDL_Event* eventPtr)
        {
            var handle = new ObjectHandle<SDL3WindowRuntime>(userdata);
            return handle.GetTarget(out SDL3WindowRuntime runtime) ? runtime.dispatchEventFromFilter(*eventPtr) : true;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static SDLBool eventWatch(IntPtr userdata, SDL_Event* eventPtr)
        {
            var handle = new ObjectHandle<SDL3WindowRuntime>(userdata);

            if (handle.GetTarget(out SDL3WindowRuntime runtime))
                runtime.dispatchEventFromWatch(*eventPtr);

            return true;
        }

        public void Dispose() => Shutdown();
    }
}
