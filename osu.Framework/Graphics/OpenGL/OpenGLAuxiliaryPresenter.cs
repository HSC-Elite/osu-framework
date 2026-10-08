// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.OpenGL.Textures;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Platform;
using osuTK.Graphics.ES30;

namespace osu.Framework.Graphics.OpenGL
{
    internal sealed class OpenGLAuxiliaryPresenter : IDisposable
    {
        private const string vertexShaderSource = """
            #version 150 core

            out vec2 v_TexCoord;

            void main()
            {
                vec2 position = gl_VertexID == 0
                    ? vec2(-1.0, -1.0)
                    : gl_VertexID == 1
                        ? vec2(3.0, -1.0)
                        : vec2(-1.0, 3.0);

                v_TexCoord = vec2((position.x + 1.0) * 0.5, (1.0 - position.y) * 0.5);
                gl_Position = vec4(position, 0.0, 1.0);
            }
            """;

        private const string fragmentShaderSource = """
            #version 150 core

            uniform sampler2D sourceTexture;
            in vec2 v_TexCoord;
            out vec4 o_Colour;

            void main()
            {
                o_Colour = texture(sourceTexture, v_TexCoord);
            }
            """;

        private readonly IOpenGLGraphicsSurface primarySurface;
        private readonly IGraphicsSurface auxiliaryGraphicsSurface;
        private readonly IOpenGLGraphicsSurface auxiliarySurface;
        private readonly ISharedOpenGLGraphicsSurface sharedAuxiliarySurface;

        private int program;
        private int vertexArray;
        private int sourceTextureLocation;
        private int validatedSourceTexture;
        private bool isDisposed;

        public OpenGLAuxiliaryPresenter(IOpenGLGraphicsSurface primarySurface, IGraphicsSurface auxiliaryGraphicsSurface)
        {
            this.primarySurface = primarySurface;
            this.auxiliaryGraphicsSurface = auxiliaryGraphicsSurface;

            auxiliarySurface = auxiliaryGraphicsSurface as IOpenGLGraphicsSurface
                               ?? throw new ArgumentException("The auxiliary surface must expose an OpenGL context.", nameof(auxiliaryGraphicsSurface));
            sharedAuxiliarySurface = auxiliaryGraphicsSurface as ISharedOpenGLGraphicsSurface
                                     ?? throw new ArgumentException("The auxiliary surface must support shared OpenGL contexts.", nameof(auxiliaryGraphicsSurface));

            sharedAuxiliarySurface.CreateSharedContext(primarySurface);

            try
            {
                makeAuxiliaryCurrent();
                vertexArray = GL.GenVertexArray();
                GL.BindVertexArray(vertexArray);
                program = createProgram();
                sourceTextureLocation = GL.GetUniformLocation(program, "sourceTexture");
            }
            catch
            {
                if (auxiliarySurface.CurrentContext == auxiliarySurface.WindowContext)
                    releaseResources();

                try
                {
                    if (auxiliarySurface.CurrentContext == auxiliarySurface.WindowContext)
                        auxiliarySurface.ClearCurrent();
                }
                finally
                {
                    sharedAuxiliarySurface.DestroySharedContext();
                }

                throw;
            }
            finally
            {
                primarySurface.MakeCurrent(primarySurface.WindowContext);
            }
        }

        public void Present(IFrameBuffer? source)
        {
            if (isDisposed)
                return;

            int textureId = source?.Texture.NativeTexture is GLTexture glTexture ? glTexture.TextureId : 0;
            if (source != null && textureId == 0)
                throw new InvalidOperationException("The auxiliary OpenGL source texture is unavailable.");

            System.Drawing.Size size = auxiliaryGraphicsSurface.GetDrawableSize();
            if (size.Width <= 0 || size.Height <= 0)
                return;

            GL.Finish();

            try
            {
                makeAuxiliaryCurrent();

                GL.BindFramebuffer(FramebufferTarget.Framebuffer, auxiliarySurface.BackbufferFramebuffer ?? 0);
                GL.Viewport(0, 0, size.Width, size.Height);
                GL.Disable(EnableCap.ScissorTest);
                GL.Disable(EnableCap.DepthTest);
                GL.Disable(EnableCap.StencilTest);
                GL.Disable(EnableCap.Blend);
                GL.ClearColor(0, 0, 0, 1);
                GL.Clear(ClearBufferMask.ColorBufferBit);

                if (textureId != 0)
                {
                    if (validatedSourceTexture != textureId)
                    {
                        if (!GL.IsTexture(textureId))
                            throw new InvalidOperationException("The auxiliary OpenGL context cannot access the source framebuffer texture.");

                        validatedSourceTexture = textureId;
                    }

                    GL.UseProgram(program);
                    GL.ActiveTexture(TextureUnit.Texture0);
                    GL.BindTexture(TextureTarget.Texture2D, textureId);
                    GL.Uniform1(sourceTextureLocation, 0);
                    GL.BindVertexArray(vertexArray);
                    GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                }

                auxiliarySurface.SwapBuffers();
            }
            finally
            {
                primarySurface.MakeCurrent(primarySurface.WindowContext);
            }
        }

        public void Dispose()
        {
            if (isDisposed)
                return;

            isDisposed = true;
            primarySurface.MakeCurrent(primarySurface.WindowContext);
            GL.Finish();

            try
            {
                makeAuxiliaryCurrent();
                releaseResources();
            }
            finally
            {
                try
                {
                    if (auxiliarySurface.CurrentContext == auxiliarySurface.WindowContext)
                        auxiliarySurface.ClearCurrent();
                }
                finally
                {
                    try
                    {
                        sharedAuxiliarySurface.DestroySharedContext();
                    }
                    finally
                    {
                        primarySurface.MakeCurrent(primarySurface.WindowContext);
                    }
                }
            }
        }

        private void makeAuxiliaryCurrent()
        {
            auxiliarySurface.MakeCurrent(auxiliarySurface.WindowContext);

            if (auxiliarySurface.CurrentContext != auxiliarySurface.WindowContext)
                throw new InvalidOperationException("Failed to make the auxiliary OpenGL context current.");
        }

        private void releaseResources()
        {
            if (vertexArray != 0)
            {
                GL.DeleteVertexArray(vertexArray);
                vertexArray = 0;
            }

            if (program != 0)
            {
                GL.DeleteProgram(program);
                program = 0;
            }
        }

        private static int createProgram()
        {
            int vertexShader = compileShader(ShaderType.VertexShader, vertexShaderSource);
            int fragmentShader = compileShader(ShaderType.FragmentShader, fragmentShaderSource);
            int shaderProgram = GL.CreateProgram();

            try
            {
                GL.AttachShader(shaderProgram, vertexShader);
                GL.AttachShader(shaderProgram, fragmentShader);
                GL.LinkProgram(shaderProgram);
                GL.GetProgram(shaderProgram, GetProgramParameterName.LinkStatus, out int linkResult);

                if (linkResult == 0)
                    throw new InvalidOperationException($"Failed to link the auxiliary OpenGL program: {GL.GetProgramInfoLog(shaderProgram)}");

                return shaderProgram;
            }
            catch
            {
                GL.DeleteProgram(shaderProgram);
                throw;
            }
            finally
            {
                GL.DeleteShader(vertexShader);
                GL.DeleteShader(fragmentShader);
            }
        }

        private static int compileShader(ShaderType type, string source)
        {
            int shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int compileResult);

            if (compileResult != 0)
                return shader;

            string error = GL.GetShaderInfoLog(shader);
            GL.DeleteShader(shader);
            throw new InvalidOperationException($"Failed to compile the auxiliary OpenGL shader: {error}");
        }
    }
}
