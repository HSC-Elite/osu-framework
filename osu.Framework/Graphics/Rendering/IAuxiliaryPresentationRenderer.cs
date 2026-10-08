// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Platform;

namespace osu.Framework.Graphics.Rendering
{
    internal interface IAuxiliaryPresentationWindow
    {
        IGraphicsSurface GraphicsSurface { get; }
        Vector2I ClientSize { get; }
        bool IsClosing { get; }
    }

    internal interface IAuxiliaryPresentationRenderer
    {
        bool SupportsAuxiliarySurface(IGraphicsSurface surface);

        void RegisterAuxiliaryWindow(IAuxiliaryPresentationWindow window);

        void SetAuxiliaryPresentationSource(IFrameBuffer frameBuffer);

        void UnregisterAuxiliaryWindow(IAuxiliaryPresentationWindow window, Action releaseWindow);
    }
}
