// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using osu.Framework.Graphics.OpenGL.Textures;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osuTK.Graphics.ES30;

namespace osu.Framework.Graphics.OpenGL
{
    internal sealed class OpenGLAuxiliaryPresenter : IDisposable
    {
        private const int make_current_warning_interval_seconds = 5;

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
        private readonly object synchronizationRoot;

        private int program;
        private int vertexArray;
        private int sourceTextureLocation;
        private int validatedSourceTexture;
        private long lastMakeCurrentWarningTimestamp;
        private bool isDisposed;

        public OpenGLAuxiliaryPresenter(IOpenGLGraphicsSurface primarySurface, IGraphicsSurface auxiliaryGraphicsSurface)
        {
            this.primarySurface = primarySurface;
            this.auxiliaryGraphicsSurface = auxiliaryGraphicsSurface;

            auxiliarySurface = auxiliaryGraphicsSurface as IOpenGLGraphicsSurface
                               ?? throw new ArgumentException("The auxiliary surface must expose an OpenGL context.", nameof(auxiliaryGraphicsSurface));
            sharedAuxiliarySurface = auxiliaryGraphicsSurface as ISharedOpenGLGraphicsSurface
                                     ?? throw new ArgumentException("The auxiliary surface must support shared OpenGL contexts.", nameof(auxiliaryGraphicsSurface));
            synchronizationRoot = sharedAuxiliarySurface.SynchronizationRoot;

            lock (synchronizationRoot)
            {
                sharedAuxiliarySurface.CreateSharedContext(primarySurface);
                bool auxiliaryWasMadeCurrent = false;

                try
                {
                    makeAuxiliaryCurrent();
                    auxiliaryWasMadeCurrent = true;
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
                    if (auxiliaryWasMadeCurrent || !isPrimaryCurrent())
                        restorePrimaryContext();
                }
            }
        }

        public void Present(IFrameBuffer? source)
        {
            lock (synchronizationRoot)
            {
                if (isDisposed)
                    return;

                int textureId = source?.Texture.NativeTexture is GLTexture glTexture ? glTexture.TextureId : 0;
                if (source != null && textureId == 0)
                    throw new InvalidOperationException("The auxiliary OpenGL source texture is unavailable.");

                System.Drawing.Size size = auxiliaryGraphicsSurface.GetDrawableSize();
                if (size.Width <= 0 || size.Height <= 0)
                    return;

                bool auxiliaryWasMadeCurrent = false;
                bool primaryRestoreAttempted = false;
                Exception? presentationException = null;

                try
                {
                    GL.Finish();

                    try
                    {
                        makeAuxiliaryCurrent();
                        auxiliaryWasMadeCurrent = true;
                    }
                    catch (Exception makeCurrentException)
                    {
                        recoverPrimaryContextAfterMakeCurrentFailure(makeCurrentException, ref primaryRestoreAttempted);
                        return;
                    }

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
                catch (Exception ex)
                {
                    presentationException = ex;
                    throw;
                }
                finally
                {
                    if (auxiliaryWasMadeCurrent ||
                        (!primaryRestoreAttempted && primarySurface.WindowContext != IntPtr.Zero && !isPrimaryCurrent()))
                    {
                        try
                        {
                            restorePrimaryContext();
                        }
                        catch (Exception restoreException) when (presentationException != null)
                        {
                            throw new AggregateException("Auxiliary OpenGL presentation failed and the primary context could not be restored.", presentationException!, restoreException);
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (synchronizationRoot)
            {
                if (isDisposed)
                    return;

                isDisposed = true;

                if (!isPrimaryCurrent())
                    restorePrimaryContext();

                GL.Finish();
                bool auxiliaryWasMadeCurrent = false;

                try
                {
                    makeAuxiliaryCurrent();
                    auxiliaryWasMadeCurrent = true;
                    releaseResources();
                }
                finally
                {
                    try
                    {
                        if (auxiliarySurface.WindowContext != IntPtr.Zero && auxiliarySurface.CurrentContext == auxiliarySurface.WindowContext)
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
                            if (auxiliaryWasMadeCurrent || !isPrimaryCurrent())
                                restorePrimaryContext();
                        }
                    }
                }
            }
        }

        private void makeAuxiliaryCurrent()
        {
            IntPtr auxiliaryContext = auxiliarySurface.WindowContext;

            if (auxiliaryContext == IntPtr.Zero)
                throw new InvalidOperationException("The auxiliary OpenGL context is unavailable.");

            auxiliarySurface.MakeCurrent(auxiliaryContext);

            if (auxiliarySurface.CurrentContext != auxiliaryContext)
                throw new InvalidOperationException("Failed to make the auxiliary OpenGL context current.");
        }

        private void recoverPrimaryContextAfterMakeCurrentFailure(Exception makeCurrentException, ref bool primaryRestoreAttempted)
        {
            IntPtr currentContext = primarySurface.CurrentContext;
            IntPtr primaryContext = primarySurface.WindowContext;
            bool primaryRestoreSucceeded = primaryContext != IntPtr.Zero && currentContext == primaryContext;
            Exception? primaryRestoreException = null;

            if (primaryContext == IntPtr.Zero)
                throw new AggregateException("Auxiliary OpenGL MakeCurrent failed and the primary context is unavailable.", makeCurrentException);

            if (!primaryRestoreSucceeded)
            {
                primaryRestoreAttempted = true;

                try
                {
                    primarySurface.MakeCurrent(primaryContext);
                    primaryRestoreSucceeded = primarySurface.CurrentContext == primaryContext;

                    if (!primaryRestoreSucceeded)
                        primaryRestoreException = new InvalidOperationException("The primary OpenGL context did not become current after the recovery attempt.");
                }
                catch (Exception ex)
                {
                    primaryRestoreException = ex;
                }
            }

            if (!primaryRestoreSucceeded)
            {
                Exception restoreException = primaryRestoreException ?? new InvalidOperationException("The primary OpenGL context could not be restored.");
                throw new AggregateException("Auxiliary OpenGL MakeCurrent failed and the primary context could not be restored.", makeCurrentException, restoreException);
            }

            logMakeCurrentWarning(currentContext, primaryContext, auxiliarySurface.WindowContext, primaryRestoreAttempted, primaryRestoreSucceeded, makeCurrentException);
        }

        private void logMakeCurrentWarning(IntPtr currentContext, IntPtr primaryContext, IntPtr auxiliaryContext, bool primaryRestoreAttempted, bool primaryRestoreSucceeded, Exception exception)
        {
            long now = Stopwatch.GetTimestamp();

            if (lastMakeCurrentWarningTimestamp != 0 && now - lastMakeCurrentWarningTimestamp < Stopwatch.Frequency * make_current_warning_interval_seconds)
                return;

            lastMakeCurrentWarningTimestamp = now;

            Logger.Log($"Auxiliary OpenGL MakeCurrent failed; skipping this presentation frame. Current context: 0x{currentContext.ToInt64():X}; expected primary context: 0x{primaryContext.ToInt64():X}; expected auxiliary context: 0x{auxiliaryContext.ToInt64():X}; primary restore attempted: {primaryRestoreAttempted}; primary restore succeeded: {primaryRestoreSucceeded}. Error: {exception.Message}", level: LogLevel.Important);
        }

        private bool isPrimaryCurrent()
            => primarySurface.WindowContext != IntPtr.Zero && primarySurface.CurrentContext == primarySurface.WindowContext;

        private void restorePrimaryContext()
        {
            IntPtr primaryContext = primarySurface.WindowContext;

            if (primaryContext == IntPtr.Zero)
                throw new InvalidOperationException("The primary OpenGL context is unavailable.");

            primarySurface.MakeCurrent(primaryContext);

            if (primarySurface.CurrentContext != primaryContext)
                throw new InvalidOperationException("Failed to restore the primary OpenGL context.");
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
