// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Dummy;
using osuTK;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class BackdropBlurSamplingTest
    {
        [Test]
        public void TestExternal720pSampling([Values(640, 1280, 1920)] int mainWidth, [Values(0.5f, 1f, 2f)] float effectScale)
        {
            IRenderer renderer = new DummyRenderer();
            renderer.PushViewport(new RectangleI(0, 0, mainWidth, mainWidth * 9 / 16));
            renderer.PushViewport(new RectangleI(0, 0, 1280, 720));

            // Layout may inherit the main window's scaling, while the external buffer stays 1280x720.
            float scale = mainWidth / 1920f;
            var externalBounds = new RectangleF(50, 100, 1280 * scale, 720 * scale);
            renderer.PushOrtho(externalBounds);
            var actual = BackdropBlurDrawNode.GetBackbufferRectangle(renderer.ProjectionMatrix);
            var blurBounds = new RectangleF(50 + 180 * scale, 100 + 650 * scale, 920 * scale, 40 * scale);
            var targetSize = new Vector2(920, 40) * effectScale;
            var destination = actual.RelativeIn(blurBounds) * targetSize;

            // UVs at the blur's edges must refer to the same pixels in the external framebuffer
            // regardless of main window dimensions or effect resolution.
            Assert.That(-destination.X / destination.Width, Is.EqualTo(180f / 1280).Within(0.00001));
            Assert.That(-destination.Y / destination.Height, Is.EqualTo(650f / 720).Within(0.00001));
            Assert.That((targetSize.X - destination.X) / destination.Width, Is.EqualTo(1100f / 1280).Within(0.00001));
            Assert.That((targetSize.Y - destination.Y) / destination.Height, Is.EqualTo(690f / 720).Within(0.00001));

            renderer.PopOrtho();
            renderer.PopViewport();
        }
    }
}
