// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Dummy;
using osuTK;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public partial class BufferedDrawNodeTest
    {
        [TestCase(1)]
        [TestCase(2)]
        public void TestPhysicalSizeIndependentOfTransform(int dpi)
        {
            using var source = new BufferedContainer
            {
                Size = new Vector2(800, 600),
                Scale = new Vector2(1.5f),
                FrameBufferScale = new Vector2(0.5f),
            };
            using var data = new BufferedDrawNodeSharedData();
            using var node = new TestDrawNode(source, data);
            node.ApplyState();
            Assert.That(node.BufferSize, Is.EqualTo(new Vector2(600, 450)));

            node.FrameBufferSizeOverride = new Vector2(800, 600) * dpi;
            node.ApplyState();
            Assert.That(node.BufferSize, Is.EqualTo(new Vector2(800, 600) * dpi));
            Assert.That(node.Rectangle.Size, Is.EqualTo(new Vector2(1200, 900)));

            // A DPI-only transition leaves layout unchanged.
            node.FrameBufferSizeOverride = new Vector2(1600, 1200);
            node.ApplyState();
            Assert.That(node.BufferSize, Is.EqualTo(new Vector2(1600, 1200)));
            Assert.That(source.DrawSize, Is.EqualTo(new Vector2(800, 600)));

            var renderer = new DummyRenderer();
            using var buffer = renderer.CreateFrameBuffer();
            node.Allocate(buffer);
            Assert.That(buffer.Size, Is.EqualTo(new Vector2(1600, 1200)));
            Assert.That(buffer.Texture.Size, Is.EqualTo(new Vector2(1600, 1200)));

            node.FrameBufferSizeOverride = new Vector2(800, 600);
            node.ApplyState();
            node.Allocate(buffer);
            Assert.That(buffer.Size, Is.EqualTo(new Vector2(800, 600)));
        }

        [Test]
        public void TestNearestBoundaryAndReparenting()
        {
            using var root = new Container { Size = new Vector2(1280, 720) };
            using var boundary = new ClipBoundary { Size = new Vector2(1920, 1080), Parent = root };
            using var source = new BufferedContainer { Position = new Vector2(1500, 800), Size = new Vector2(600, 400), Parent = boundary };
            using var data = new BufferedDrawNodeSharedData(clipToRootNode: true);
            using var node = new TestDrawNode(source, data);
            node.ApplyState();
            Assert.That(node.Rectangle, Is.EqualTo(new RectangleF(1500, 800, 420, 280)));
            Assert.That(node.BufferSize, Is.EqualTo(new Vector2(420, 280)));

            using var innerBoundary = new ClipBoundary { Size = new Vector2(1700, 900), Parent = boundary };
            source.Parent = null!;
            source.Parent = innerBoundary;
            node.ApplyState();
            Assert.That(node.Rectangle, Is.EqualTo(new RectangleF(1500, 800, 200, 100)));

            source.Parent = null!;
            source.Parent = root;
            node.ApplyState();
            Assert.That(node.Rectangle.IsEmpty, Is.True);

            using var ordinarySource = new BufferedContainer { Position = new Vector2(1000, 600), Size = new Vector2(600, 400), Parent = root };
            using var ordinaryNode = new TestDrawNode(ordinarySource, data);
            ordinaryNode.ApplyState();
            Assert.That(ordinaryNode.Rectangle, Is.EqualTo(new RectangleF(1000, 600, 280, 120)));
        }

        [TestCase(300, 200)]
        [TestCase(200, 120)]
        public void TestExternalWindowClippingAndReparenting(float windowSize, float expectedSize)
        {
            using var root = new Container { Size = new Vector2(100) };
            using var window = new ExternalWindowContainer { Size = new Vector2(windowSize) };
            using var ancestor = new Container { Size = new Vector2(300) };
            using var source = new BufferedContainer { Position = new Vector2(80), Size = new Vector2(200) };
            // Establish the layout hierarchy without loading native windows or GPU resources.
            window.Parent = root;
            ancestor.Parent = window;
            source.Parent = ancestor;

            using var data = new BufferedDrawNodeSharedData(clipToRootNode: true);
            using var node = new TestDrawNode(source, data);
            node.ApplyState();
            Assert.That(node.Rectangle, Is.EqualTo(new RectangleF(80, 80, expectedSize, expectedSize)));

            ancestor.Parent = null!;
            ancestor.Parent = root;
            node.ApplyState();
            Assert.That(node.Rectangle, Is.EqualTo(new RectangleF(80, 80, 20, 20)));
        }

        private partial class ClipBoundary : Container, IBufferedDrawNodeClipBoundary;

        private class TestDrawNode : BufferedDrawNode
        {
            public Vector2 BufferSize => GetFrameBufferSize(null!);
            public RectangleF Rectangle => DrawRectangle;

            public void Allocate(IFrameBuffer buffer)
            {
                using (BindFrameBuffer(buffer))
                {
                }
            }

            public TestDrawNode(BufferedContainer source, BufferedDrawNodeSharedData data)
                : base(source, new DrawNode(source), data)
            {
            }
        }
    }
}
