// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Development;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;
using Veldrid;
using Vortice.Direct3D11;
using SharpGen.Runtime;
using SamplerDescription = Veldrid.SamplerDescription;
using Texture = osu.Framework.Graphics.Textures.Texture;

namespace osu.Framework.Graphics.Veldrid.Textures
{
    /// <summary>
    /// A texture that can be updated directly from a native D3D11 texture via GPU copy.
    /// </summary>
    public sealed class D3D11ExternalTexture : Texture
    {
        private readonly D3D11ExternalNativeTexture native;

        public D3D11ExternalTexture(IRenderer renderer, int width, int height, TextureFilteringMode filteringMode = TextureFilteringMode.Linear)
            : base(new D3D11ExternalNativeTexture(renderer, width, height, filteringMode), WrapMode.None, WrapMode.None)
        {
            native = (D3D11ExternalNativeTexture)NativeTexture;
        }

        /// <summary>
        /// Copies from <paramref name="source"/> into the internal GPU texture.
        /// Must be called on the draw thread.
        /// </summary>
        public void UpdateFrom(ID3D11Texture2D source)
            => native.UpdateFrom(source);
    }

    internal sealed class D3D11ExternalNativeTexture : VeldridTexture
    {
        private readonly BackendInfoD3D11 d3dInfo;
        private readonly ID3D11Device d3dDevice;
        private ID3D11DeviceContext? d3dContext;
        private readonly SamplerFilter samplerFilter;

        private VeldridTextureResources[]? resourceList;
        private ID3D11Texture2D? d3dTargetTexture;
        private int textureSize;

        public D3D11ExternalNativeTexture(IRenderer renderer, int width, int height, TextureFilteringMode filteringMode)
            : base(getVeldridRenderer(renderer), width, height, true, filteringMode.ToSamplerFilter())
        {
            samplerFilter = filteringMode.ToSamplerFilter();

            if (!D3D11Interop.TryGetD3D11Device(renderer, out d3dDevice, out var context, out d3dInfo))
                throw new InvalidOperationException("Renderer is not a Veldrid D3D11 renderer.");

            d3dContext = context;
            ensureResources(width, height);
        }

        public override int GetByteSize() => textureSize;

        public void UpdateFrom(ID3D11Texture2D source)
        {
            ThreadSafety.EnsureDrawThread();

            var desc = source.Description;
            ensureResources(desc.Width, desc.Height);

            if (d3dTargetTexture == null)
            {
                Logger.Log("D3D11 external texture target was not initialised.", level: LogLevel.Error);
                return;
            }

            d3dContext ??= d3dDevice.ImmediateContext;

            if (d3dContext == null)
            {
                Logger.Log("D3D11 immediate context was not available.", level: LogLevel.Error);
                return;
            }

            d3dContext.CopyResource(d3dTargetTexture, source);
        }

        public override IReadOnlyList<VeldridTextureResources> GetResourceList()
            => resourceList.AsNonNull();

        private void ensureResources(int width, int height)
        {
            if (resourceList != null && Width == width && Height == height)
                return;

            disposeResources();

            Width = width;
            Height = height;
            textureSize = width * height * 4;

            var textureDescription = TextureDescription.Texture2D((uint)width, (uint)height, 1, 1, PixelFormat.B8G8R8A8UNorm, TextureUsage.Sampled);
            var texture = Renderer.Factory.CreateTexture(ref textureDescription);

            var sampler = Renderer.Factory.CreateSampler(new SamplerDescription
            {
                AddressModeU = SamplerAddressMode.Clamp,
                AddressModeV = SamplerAddressMode.Clamp,
                AddressModeW = SamplerAddressMode.Clamp,
                Filter = samplerFilter,
                MinimumLod = 0,
                MaximumLod = IRenderer.MAX_MIPMAP_LEVELS,
                MaximumAnisotropy = 0,
            });

            resourceList = new[] { new VeldridTextureResources(texture, sampler) };

            var nativePtr = d3dInfo.GetTexturePointer(texture);
            d3dTargetTexture = MarshallingHelpers.FromPointer<ID3D11Texture2D>(nativePtr).AsNonNull();
        }

        private void disposeResources()
        {
            if (resourceList == null)
                return;

            foreach (var res in resourceList)
                res.Dispose();

            resourceList = null;
            d3dTargetTexture = null;
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            Renderer.ScheduleDisposal(texture =>
            {
                texture.disposeResources();
            }, this);
        }

        private static IVeldridRenderer getVeldridRenderer(IRenderer renderer)
        {
            if (renderer is VeldridRenderer veldridRenderer)
                return veldridRenderer;

            throw new InvalidOperationException("Renderer is not a Veldrid renderer.");
        }
    }
}
