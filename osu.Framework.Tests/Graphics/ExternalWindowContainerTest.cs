// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Primitives;
using osuTK;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class ExternalWindowContainerTest
    {
        [Test]
        public void TestRenderCoordinatesMatchParent([Values(0.5f, 1f, 2f)] float parentScale)
        {
            using var parent = new Container
            {
                Size = new Vector2(640, 360),
                Scale = new Vector2(parentScale),
                Position = new Vector2(50, 100),
            };
            using var window = new ExternalWindowContainer { Size = new Vector2(1280, 720), Parent = parent };
            using var child = new BufferedContainer { Position = new Vector2(1000, 600), Size = new Vector2(200, 100), Parent = window };
            using var data = new BufferedDrawNodeSharedData(clipToRootNode: true);
            using var node = new TestBufferedDrawNode(child, data);
            node.ApplyState();

            // The provider and its descendants must describe the same screen coordinate system.
            var expected = new RectangleF(50 + 1000 * parentScale, 100 + 600 * parentScale, 200 * parentScale, 100 * parentScale);
            Assert.That(child.ScreenSpaceDrawQuad.AABBFloat, Is.EqualTo(expected));
            // This child is outside the main window but inside the external render boundary.
            Assert.That(node.Rectangle, Is.EqualTo(expected));
            Assert.That(child.ToLocalSpace(new Vector2(expected.X, expected.Y)), Is.EqualTo(Vector2.Zero));
        }

        private class TestBufferedDrawNode : BufferedDrawNode
        {
            public RectangleF Rectangle => DrawRectangle;

            public TestBufferedDrawNode(BufferedContainer source, BufferedDrawNodeSharedData data)
                : base(source, new DrawNode(source), data)
            {
            }
        }
    }
}
