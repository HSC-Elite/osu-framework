// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using osu.Framework.Development;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Textures;
using osuTK.Graphics.ES30;

namespace osu.Framework.Graphics.OpenGL.Textures
{
    public readonly struct DmaBufExternalTexturePlane
    {
        public int Stride { get; }
        public int Offset { get; }
        public int FileDescriptor { get; }

        public DmaBufExternalTexturePlane(int stride, int offset, int fileDescriptor)
        {
            Stride = stride;
            Offset = offset;
            FileDescriptor = fileDescriptor;
        }
    }

    /// <summary>
    /// An OpenGL texture backed by an imported Linux DMA-BUF.
    /// </summary>
    public sealed class DmaBufExternalTexture : Texture
    {
        /// <summary>
        /// Creates a texture which imports <paramref name="planes"/> through the current EGL display.
        /// The caller retains ownership of the plane file descriptors and may close them after this method returns.
        /// </summary>
        public DmaBufExternalTexture(IRenderer renderer, int width, int height, uint fourcc, ulong modifier, IReadOnlyList<DmaBufExternalTexturePlane> planes)
            : base(new DmaBufExternalNativeTexture(renderer, width, height, fourcc, modifier, planes), WrapMode.None, WrapMode.None)
        {
        }

        /// <summary>
        /// Attempts to import an RGB DMA-BUF as an OpenGL texture.
        /// </summary>
        public static bool TryCreate(IRenderer renderer, int width, int height, uint fourcc, ulong modifier, IReadOnlyList<DmaBufExternalTexturePlane> planes,
                                     out DmaBufExternalTexture? texture, out string? failure)
        {
            try
            {
                texture = new DmaBufExternalTexture(renderer, width, height, fourcc, modifier, planes);
                failure = null;
                return true;
            }
            catch (Exception e)
            {
                texture = null;
                failure = e.Message;
                return false;
            }
        }
    }

    internal sealed class DmaBufExternalNativeTexture : GLTexture
    {
        private const int egl_extensions = 0x3055;
        private const int egl_none = 0x3038;
        private const int egl_linux_dma_buf_ext = 0x3270;
        private const int egl_width = 0x3057;
        private const int egl_height = 0x3056;
        private const int egl_linux_drm_fourcc_ext = 0x3271;
        private const int egl_dma_buf_plane0_fd_ext = 0x3272;
        private const int egl_dma_buf_plane0_offset_ext = 0x3273;
        private const int egl_dma_buf_plane0_pitch_ext = 0x3274;
        private const int egl_dma_buf_plane0_modifier_lo_ext = 0x3443;
        private const int egl_dma_buf_plane0_modifier_hi_ext = 0x3444;
        private const int egl_dma_buf_plane1_fd_ext = 0x3275;
        private const int egl_dma_buf_plane1_offset_ext = 0x3276;
        private const int egl_dma_buf_plane1_pitch_ext = 0x3277;
        private const int egl_dma_buf_plane1_modifier_lo_ext = 0x3445;
        private const int egl_dma_buf_plane1_modifier_hi_ext = 0x3446;
        private const ulong drm_format_mod_invalid = 0x00ff_ffff_ffff_ffff;

        private const uint drm_format_xrgb8888 = 0x34325258;
        private const uint drm_format_argb8888 = 0x34325241;
        private const uint drm_format_xbgr8888 = 0x34324258;
        private const uint drm_format_abgr8888 = 0x34324241;

        private readonly IntPtr display;
        private readonly IntPtr image;
        private readonly DestroyImageDelegate destroyImage;
        private int textureId;
        private bool disposed;

        public DmaBufExternalNativeTexture(IRenderer renderer, int width, int height, uint fourcc, ulong modifier, IReadOnlyList<DmaBufExternalTexturePlane> planes)
            : base(getRenderer(renderer), width, height, manualMipmaps: true)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

            if (planes.Count is < 1 or > 2)
                throw new NotSupportedException($"DMA-BUF import supports one or two planes, but received {planes.Count}.");

            foreach (var plane in planes)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(plane.Stride);
                ArgumentOutOfRangeException.ThrowIfNegative(plane.Offset);
                ArgumentOutOfRangeException.ThrowIfNegative(plane.FileDescriptor);
            }

            if (!isSupportedFormat(fourcc))
                throw new NotSupportedException($"Unsupported DMA-BUF fourcc 0x{fourcc:x8}.");

            ThreadSafety.EnsureDrawThread();

            display = eglGetCurrentDisplay();

            if (display == IntPtr.Zero)
                throw new NotSupportedException("The current OpenGL context does not expose an EGL display.");

            string extensions = Marshal.PtrToStringAnsi(eglQueryString(display, egl_extensions)) ?? string.Empty;

            if (!extensions.Contains("EGL_EXT_image_dma_buf_import", StringComparison.Ordinal))
                throw new NotSupportedException("EGL_EXT_image_dma_buf_import is unavailable.");

            if (modifier != drm_format_mod_invalid && !extensions.Contains("EGL_EXT_image_dma_buf_import_modifiers", StringComparison.Ordinal))
                throw new NotSupportedException("EGL_EXT_image_dma_buf_import_modifiers is unavailable.");

            IntPtr createImagePtr = Renderer.GetProcAddress("eglCreateImageKHR");
            IntPtr destroyImagePtr = Renderer.GetProcAddress("eglDestroyImageKHR");
            IntPtr imageTargetPtr = Renderer.GetProcAddress("glEGLImageTargetTexture2DOES");

            if (createImagePtr == IntPtr.Zero || destroyImagePtr == IntPtr.Zero || imageTargetPtr == IntPtr.Zero)
                throw new NotSupportedException("Required EGL DMA-BUF import entry points are unavailable.");

            var createImage = Marshal.GetDelegateForFunctionPointer<CreateImageDelegate>(createImagePtr);
            destroyImage = Marshal.GetDelegateForFunctionPointer<DestroyImageDelegate>(destroyImagePtr);
            var imageTarget = Marshal.GetDelegateForFunctionPointer<ImageTargetTextureDelegate>(imageTargetPtr);

            int[] attributes = createAttributes(width, height, fourcc, modifier, planes);
            image = createImage(display, IntPtr.Zero, egl_linux_dma_buf_ext, IntPtr.Zero, attributes);

            if (image == IntPtr.Zero)
                throw new InvalidOperationException("eglCreateImageKHR() failed to import the DMA-BUF.");

            try
            {
                int[] textures = new int[1];
                GL.GenTextures(1, textures);
                textureId = textures[0];
                GL.BindTexture(TextureTarget.Texture2D, textureId);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)All.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)All.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)All.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)All.ClampToEdge);
                imageTarget((int)TextureTarget.Texture2D, image);
            }
            catch
            {
                if (textureId != 0)
                    GL.DeleteTextures(1, new[] { textureId });
                    textureId = 0;

                destroyImage(display, image);
                throw;
            }
        }

        public override int TextureId
        {
            get
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return textureId;
            }
        }

        public override int GetByteSize() => Width * Height * 4;

        protected override void Dispose(bool isDisposing)
        {
            if (disposed)
                return;

            disposed = true;
            Renderer.ScheduleDisposal(texture => texture.destroy(), this);
        }

        private void destroy()
        {
            if (textureId != 0)
            {
                GL.DeleteTextures(1, new[] { textureId });
                textureId = 0;
            }

            destroyImage(display, image);
        }

        private static GLRenderer getRenderer(IRenderer renderer)
        {
            if (renderer is GLRenderer glRenderer)
                return glRenderer;

            throw new NotSupportedException("DMA-BUF import requires osu!framework's OpenGL renderer.");
        }

        private static bool isSupportedFormat(uint fourcc)
            => fourcc is drm_format_xrgb8888 or drm_format_argb8888 or drm_format_xbgr8888 or drm_format_abgr8888;

        private static int[] createAttributes(int width, int height, uint fourcc, ulong modifier, IReadOnlyList<DmaBufExternalTexturePlane> planes)
        {
            var attributes = new List<int>
            {
                egl_width, width,
                egl_height, height,
                egl_linux_drm_fourcc_ext, unchecked((int)fourcc),
            };

            for (int i = 0; i < planes.Count; i++)
            {
                var plane = planes[i];
                int fdAttribute = i == 0 ? egl_dma_buf_plane0_fd_ext : egl_dma_buf_plane1_fd_ext;
                int offsetAttribute = i == 0 ? egl_dma_buf_plane0_offset_ext : egl_dma_buf_plane1_offset_ext;
                int pitchAttribute = i == 0 ? egl_dma_buf_plane0_pitch_ext : egl_dma_buf_plane1_pitch_ext;

                attributes.Add(fdAttribute);
                attributes.Add(plane.FileDescriptor);
                attributes.Add(offsetAttribute);
                attributes.Add(plane.Offset);
                attributes.Add(pitchAttribute);
                attributes.Add(plane.Stride);

                if (modifier == drm_format_mod_invalid)
                    continue;

                int modifierLoAttribute = i == 0 ? egl_dma_buf_plane0_modifier_lo_ext : egl_dma_buf_plane1_modifier_lo_ext;
                int modifierHiAttribute = i == 0 ? egl_dma_buf_plane0_modifier_hi_ext : egl_dma_buf_plane1_modifier_hi_ext;
                attributes.Add(modifierLoAttribute);
                attributes.Add(unchecked((int)modifier));
                attributes.Add(modifierHiAttribute);
                attributes.Add(unchecked((int)(modifier >> 32)));
            }

            attributes.Add(egl_none);
            return attributes.ToArray();
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CreateImageDelegate(IntPtr display, IntPtr context, int target, IntPtr buffer, int[] attributes);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool DestroyImageDelegate(IntPtr display, IntPtr image);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ImageTargetTextureDelegate(int target, IntPtr image);

        [DllImport("libEGL", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr eglGetCurrentDisplay();

        [DllImport("libEGL", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr eglQueryString(IntPtr display, int name);
    }
}
