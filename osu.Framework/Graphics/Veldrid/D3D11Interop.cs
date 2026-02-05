// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics.Rendering;
using SharpGen.Runtime;
using Veldrid;
using Vortice.Direct3D11;

namespace osu.Framework.Graphics.Veldrid
{
    /// <summary>
    /// Helpers for working with native Direct3D 11 objects when the renderer is Veldrid D3D11.
    /// </summary>
    public static class D3D11Interop
    {
        /// <summary>
        /// Tries to retrieve the native D3D11 device and immediate context for a Veldrid D3D11 renderer.
        /// </summary>
        public static bool TryGetD3D11Device(IRenderer renderer, out ID3D11Device device, out ID3D11DeviceContext context, out BackendInfoD3D11 info)
        {
            device = null!;
            context = null!;
            info = default;

            if (renderer is not VeldridRenderer veldridRenderer)
                return false;

            if (veldridRenderer.Device.BackendType != GraphicsBackend.Direct3D11)
                return false;

            info = veldridRenderer.Device.GetD3D11Info();
            device = MarshallingHelpers.FromPointer<ID3D11Device>(info.Device).AsNonNull();
            context = device.ImmediateContext;
            return context != null;
        }
    }
}
