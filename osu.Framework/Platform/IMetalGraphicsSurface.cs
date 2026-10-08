// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Framework.Platform
{
    /// <summary>
    /// Represents Metal-specific graphics API provided by an <see cref="IWindow"/>.
    /// </summary>
    public interface IMetalGraphicsSurface
    {
        /// <summary>
        /// Returns a platform view backed with a Metal layer for use as a presentation surface.
        /// </summary>
        IntPtr CreateMetalView();
    }
}
