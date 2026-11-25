using System;
using NUnit.Framework;
using Brick = UnityEngine.Rendering.ProbeBrickIndex.Brick;

namespace UnityEngine.Rendering.Tests
{
    public class ProbeVolumeLayoutTests
    {
        [TestCase(0, 1)]
        [TestCase(1, 3)]
        [TestCase(2, 9)]
        [TestCase(5, 243)]
        public void SubdivisionUsesThreeToOneBrickHierarchy(int subdivision, int size)
        {
            Assert.That(ProbeVolumeLayout.CellSize(subdivision), Is.EqualTo(size));
        }

        [Test]
        public void LayoutDimensionsMatchProbeSpacing()
        {
            var layout = new ProbeVolumeLayout(2, 0.5f, Vector3.zero);

            Assert.That(layout.maxSubdivision, Is.EqualTo(3));
            Assert.That(layout.minBrickSize, Is.EqualTo(1.5f));
            Assert.That(layout.cellSizeInBricks, Is.EqualTo(9));
            Assert.That(layout.cellSizeInMeters, Is.EqualTo(13.5f));
            Assert.That(layout.BrickSize(1), Is.EqualTo(4.5f));
        }

        [Test]
        public void PositionToCellUsesOffsetAndFloorsNegativeCoordinates()
        {
            var layout = new ProbeVolumeLayout(2, 0.5f, new Vector3(10, -20, 30));

            Assert.That(layout.PositionToCell(layout.probeOffset), Is.EqualTo(Vector3Int.zero));
            Assert.That(layout.PositionToCell(new Vector3(9.875f, -20.125f, 29.875f)), Is.EqualTo(new Vector3Int(-1, -1, -1)));
            Assert.That(layout.PositionToCell(new Vector3(-3.5f, -6.5f, 57)), Is.EqualTo(new Vector3Int(-1, 1, 2)));
            Assert.That(layout.PositionToCell(new Vector3(-3.625f, -6.625f, 56.875f)), Is.EqualTo(new Vector3Int(-2, 0, 1)));
        }

        [TestCase(0, 0.5f)]
        [TestCase(1, 1.5f)]
        [TestCase(2, 4.5f)]
        public void BrickPositionsKeepWorldOriginAndXFastestOrdering(int subdivision, float spacing)
        {
            var layout = new ProbeVolumeLayout(3, 0.5f, new Vector3(10, -20, 30));
            var bricks = new[] { new Brick(new Vector3Int(-9, 3, -6), subdivision) };

            ProbeVolumePlacement.ConvertBricksToPositions(layout, bricks, out var positions, out var subdivisions);

            var first = new Vector3(-3.5f, -15.5f, 21);
            Assert.That(positions, Has.Length.EqualTo(64));
            Assert.That(subdivisions, Has.Length.EqualTo(64));
            Assert.That(positions[0], Is.EqualTo(first));
            Assert.That(positions[1], Is.EqualTo(first + Vector3.right * spacing));
            Assert.That(positions[4], Is.EqualTo(first + Vector3.up * spacing));
            Assert.That(positions[16], Is.EqualTo(first + Vector3.forward * spacing));
            Assert.That(positions[63], Is.EqualTo(first + Vector3.one * (3 * spacing)));
            Assert.That(subdivisions, Is.All.EqualTo(subdivision));
        }

        [Test]
        public void AdjacentBricksShareProbePositionsOnTheirCommonFace()
        {
            var layout = new ProbeVolumeLayout(2, 0.5f, new Vector3(10, -20, 30));
            var bricks = new[] { new Brick(new Vector3Int(-3, 0, 0), 1), new Brick(Vector3Int.zero, 1) };

            ProbeVolumePlacement.ConvertBricksToPositions(layout, bricks, out var positions, out _);

            Assert.That(positions[3], Is.EqualTo(positions[64]));
            Assert.That(positions[15], Is.EqualTo(positions[76]));
            Assert.That(positions[51], Is.EqualTo(positions[112]));
            Assert.That(positions[63], Is.EqualTo(positions[124]));
        }

        [Test]
        public void CapturedBakedLayoutRetainsGeometryAfterPlacementSettingsChange()
        {
            var layout = new ProbeVolumeLayout(2, 0.5f, new Vector3(10, -20, 30));
            var bakedLayout = layout;
            layout = new ProbeVolumeLayout(3, 1, new Vector3(-10, 20, -30));
            var bricks = new[] { new Brick(new Vector3Int(1, 0, 0), 0) };

            ProbeVolumePlacement.ConvertBricksToPositions(bakedLayout, bricks, out var bakedPositions, out _);
            ProbeVolumePlacement.ConvertBricksToPositions(layout, bricks, out var newPositions, out _);

            Assert.That(bakedLayout.cellSizeInMeters, Is.EqualTo(13.5f));
            Assert.That(layout.cellSizeInMeters, Is.EqualTo(81));
            Assert.That(bakedPositions[0], Is.EqualTo(new Vector3(11.5f, -20, 30)));
            Assert.That(bakedPositions[63], Is.EqualTo(new Vector3(13, -18.5f, 31.5f)));
            Assert.That(newPositions[0], Is.EqualTo(new Vector3(-7, 20, -30)));
            Assert.That(newPositions[63], Is.EqualTo(new Vector3(-4, 23, -27)));
        }

        [Test]
        public void EmptyLayoutProducesNoProbePositions()
        {
            var layout = new ProbeVolumeLayout(2, 0.5f, Vector3.zero);

            ProbeVolumePlacement.ConvertBricksToPositions(layout, Array.Empty<Brick>(), out var positions, out var subdivisions);

            Assert.That(positions, Is.Empty);
            Assert.That(subdivisions, Is.Empty);
        }
    }
}
