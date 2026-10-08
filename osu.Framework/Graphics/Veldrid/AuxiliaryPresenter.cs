// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Veldrid.Buffers;
using osu.Framework.Graphics.Veldrid.Textures;
using osu.Framework.Platform;
using osuTK;
using Veldrid;
using Veldrid.SPIRV;
using PrimitiveTopology = Veldrid.PrimitiveTopology;

namespace osu.Framework.Graphics.Veldrid
{
    internal sealed class AuxiliaryBlitter : IDisposable
    {
        private const string vertexShaderSource = """
            #version 450

            layout(location = 0) out vec2 v_TexCoord;

            void main()
            {
                vec2 position = gl_VertexIndex == 0
                    ? vec2(-1.0, -1.0)
                    : gl_VertexIndex == 1
                        ? vec2(3.0, -1.0)
                        : vec2(-1.0, 3.0);

                v_TexCoord = vec2((position.x + 1.0) * 0.5, (1.0 - position.y) * 0.5);
                gl_Position = vec4(position, 0.0, 1.0);
            }
            """;

        private const string fragmentShaderSource = """
            #version 450

            layout(set = 0, binding = 0) uniform texture2D sourceTexture;
            layout(set = 0, binding = 1) uniform sampler sourceSampler;
            layout(location = 0) in vec2 v_TexCoord;
            layout(location = 0) out vec4 o_Colour;

            void main()
            {
                o_Colour = texture(sampler2D(sourceTexture, sourceSampler), v_TexCoord);
            }
            """;

        private readonly ResourceFactory factory;
        private readonly GraphicsDevice device;
        private readonly CommandList commands;
        private readonly ResourceLayout textureLayout;
        private readonly Shader[] shaders;
        private readonly Pipeline pipeline;
        private readonly Dictionary<Texture, ResourceSet> textureResourceSets = new Dictionary<Texture, ResourceSet>();

        public AuxiliaryBlitter(ResourceFactory factory, GraphicsDevice device, OutputDescription outputDescription)
        {
            this.factory = factory;
            this.device = device;

            shaders = factory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(vertexShaderSource), "main"),
                new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(fragmentShaderSource), "main"));

            textureLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("sourceTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("sourceSampler", ResourceKind.Sampler, ShaderStages.Fragment)));

            var pipelineDescription = new GraphicsPipelineDescription(
                BlendStateDescription.SINGLE_OVERRIDE_BLEND,
                new DepthStencilStateDescription(false, false, ComparisonKind.Always),
                RasterizerStateDescription.CULL_NONE,
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(Array.Empty<VertexLayoutDescription>(), shaders),
                new[] { textureLayout },
                outputDescription);

            pipeline = factory.CreateGraphicsPipeline(ref pipelineDescription);
            commands = factory.CreateCommandList();
        }

        public void Blit(IFrameBuffer source, Framebuffer target, int width, int height)
        {
            var texture = (VeldridTexture)source.Texture.NativeTexture;
            VeldridTextureResources textureResources = texture.GetResourceList().Single();

            if (!textureResourceSets.TryGetValue(textureResources.Texture, out ResourceSet? textureResourceSet))
            {
                if (textureResources.Sampler == null)
                    throw new InvalidOperationException("The auxiliary blitter source texture has no sampler.");

                textureResourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                    textureLayout,
                    textureResources.Texture,
                    textureResources.Sampler));

                textureResourceSets.Add(textureResources.Texture, textureResourceSet);
            }

            commands.Begin();
            commands.SetFramebuffer(target);
            commands.ClearColorTarget(0, new RgbaFloat(0, 0, 0, 1));
            commands.SetViewport(0, new Viewport(0, 0, width, height, 0, 1));
            commands.SetScissorRect(0, 0, 0, (uint)width, (uint)height);
            commands.SetPipeline(pipeline);
            commands.SetGraphicsResourceSet(0, textureResourceSet);
            commands.Draw(3);
            commands.End();

            device.SubmitCommands(commands);
        }

        public void Dispose()
        {
            foreach (ResourceSet textureResourceSet in textureResourceSets.Values)
                textureResourceSet.Dispose();

            textureResourceSets.Clear();

            commands.Dispose();
            pipeline.Dispose();
            textureLayout.Dispose();

            foreach (Shader shader in shaders)
                shader.Dispose();
        }
    }

    internal sealed class AuxiliaryPresenter : IDisposable
    {
        private readonly VeldridDevice device;
        private readonly Swapchain swapchain;
        private readonly AuxiliaryBlitter blitter;

        private Vector2I size;
        private bool isDisposed;

        public AuxiliaryPresenter(VeldridDevice device, IGraphicsSurface graphicsSurface, Vector2I initialSize)
        {
            this.device = device;
            size = initialSize;

            swapchain = device.Factory.CreateSwapchain(new SwapchainDescription
            {
                Source = VeldridDevice.CreateSwapchainSource(graphicsSurface)
                         ?? throw new NotSupportedException($"No swapchain source is available for {graphicsSurface.Type}."),
                Width = (uint)size.X,
                Height = (uint)size.Y,
                DepthFormat = PixelFormat.R16UNorm,
                SyncToVerticalBlank = false,
                ColorSrgb = false,
            });

            blitter = new AuxiliaryBlitter(device.Factory, device.Device, swapchain.Framebuffer.OutputDescription);
        }

        public void Resize(Vector2I newSize)
        {
            if (newSize.X <= 0 || newSize.Y <= 0 || newSize == size)
                return;

            device.WaitUntilIdle();
            swapchain.Resize((uint)newSize.X, (uint)newSize.Y);
            size = newSize;
        }

        public void Blit(IFrameBuffer source)
        {
            if (size.X <= 0 || size.Y <= 0)
                return;

            blitter.Blit(source, swapchain.Framebuffer, size.X, size.Y);
        }

        public void SwapBuffers() => device.Device.SwapBuffers(swapchain);

        public void Dispose()
        {
            if (isDisposed)
                return;

            isDisposed = true;

            blitter.Dispose();
            swapchain.Dispose();
        }
    }
}
