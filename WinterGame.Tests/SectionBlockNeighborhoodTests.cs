using WinterGame.World;

namespace WinterGame.Tests;

public class SectionBlockNeighborhoodTests
{
    private const int BlockSize = SectionBlockPos.BlockSize;

    [Fact]
    public void Erode_MatchesABruteForceScanOverRandomNeighborhoods()
    {
        var random = new Random(1234);

        // Most sections set, since a 3x3x3 erosion of a sparse neighborhood is almost always empty:
        foreach (var density in new[] { 0.9, 0.97, 0.995, 1.0 })
        {
            for (var round = 0; round < 200; round++)
            {
                var masks = RandomNeighborhood(random, density);

                Assert.Equal(BruteForce(masks, all: true), SectionBlockNeighborhood.Erode(masks));
            }
        }
    }

    [Fact]
    public void Dilate_MatchesABruteForceScanOverRandomNeighborhoods()
    {
        var random = new Random(4321);

        foreach (var density in new[] { 0.0, 0.002, 0.01, 0.05 })
        {
            for (var round = 0; round < 200; round++)
            {
                var masks = RandomNeighborhood(random, density);

                Assert.Equal(BruteForce(masks, all: false), SectionBlockNeighborhood.Dilate(masks));
            }
        }
    }

    [Fact]
    public void ErodeAndDilate_OfOneSectionInEachBlock_ReachOnlyTheSectionsBesideIt()
    {
        for (var block = 0; block < SectionBlockNeighborhood.Count; block++)
        {
            for (var localIndex = 0; localIndex < SectionBlockPos.SectionsPerBlock; localIndex++)
            {
                var masks = new ulong[SectionBlockNeighborhood.Count];

                masks[block] = 1UL << localIndex;

                Assert.Equal(BruteForce(masks, all: false), SectionBlockNeighborhood.Dilate(masks));

                Array.Fill(masks, ulong.MaxValue);
                masks[block] = ~(1UL << localIndex);

                Assert.Equal(BruteForce(masks, all: true), SectionBlockNeighborhood.Erode(masks));
            }
        }
    }

    [Fact]
    public void GetBorder_HoldsTheSectionsWhoseNeighborhoodReachesTheBlock()
    {
        for (var offsetZ = -1; offsetZ <= 1; offsetZ++)
        {
            for (var offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (var offsetX = -1; offsetX <= 1; offsetX++)
                {
                    var expected = 0UL;

                    for (var localIndex = 0; localIndex < SectionBlockPos.SectionsPerBlock; localIndex++)
                    {
                        var section = new SectionBlockPos(0, 0, 0).GetSection(localIndex);

                        if (Reaches(section.X, offsetX) && Reaches(section.Y, offsetY) && Reaches(section.Z, offsetZ))
                        {
                            expected |= 1UL << localIndex;
                        }
                    }

                    Assert.Equal(expected, SectionBlockNeighborhood.GetBorder(offsetX, offsetY, offsetZ));
                }
            }
        }
    }

    /// <summary>
    ///     Whether the three sections around <paramref name="coordinate"/> along one axis include one in the block at <paramref name="offset"/>.
    /// </summary>
    private static bool Reaches(int coordinate, int offset)
    {
        for (var step = -1; step <= 1; step++)
        {
            if ((coordinate + step) >> SectionBlockPos.Log == offset)
            {
                return true;
            }
        }

        return false;
    }

    private static ulong[] RandomNeighborhood(Random random, double density)
    {
        var masks = new ulong[SectionBlockNeighborhood.Count];

        for (var block = 0; block < masks.Length; block++)
        {
            for (var localIndex = 0; localIndex < SectionBlockPos.SectionsPerBlock; localIndex++)
            {
                if (random.NextDouble() < density)
                {
                    masks[block] |= 1UL << localIndex;
                }
            }
        }

        return masks;
    }

    /// <summary>
    ///     Lays the 27 blocks out as one 12x12x12 grid of sections and tests the 3x3x3 sections around each section of the center block.
    /// </summary>
    private static ulong BruteForce(ulong[] masks, bool all)
    {
        const int gridSize = 3 * BlockSize;

        var grid = new bool[gridSize, gridSize, gridSize];

        for (var offsetZ = -1; offsetZ <= 1; offsetZ++)
        {
            for (var offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (var offsetX = -1; offsetX <= 1; offsetX++)
                {
                    var mask = masks[SectionBlockNeighborhood.GetIndex(offsetX, offsetY, offsetZ)];

                    for (var localIndex = 0; localIndex < SectionBlockPos.SectionsPerBlock; localIndex++)
                    {
                        var section = new SectionBlockPos(offsetX + 1, offsetY + 1, offsetZ + 1).GetSection(localIndex);

                        grid[section.X, section.Y, section.Z] = (mask & (1UL << localIndex)) != 0;
                    }
                }
            }
        }

        var result = 0UL;

        for (var localIndex = 0; localIndex < SectionBlockPos.SectionsPerBlock; localIndex++)
        {
            var center = new SectionBlockPos(1, 1, 1).GetSection(localIndex);
            var count = 0;

            for (var z = center.Z - 1; z <= center.Z + 1; z++)
            {
                for (var y = center.Y - 1; y <= center.Y + 1; y++)
                {
                    for (var x = center.X - 1; x <= center.X + 1; x++)
                    {
                        if (grid[x, y, z])
                        {
                            count++;
                        }
                    }
                }
            }

            if (all ? count == 27 : count > 0)
            {
                result |= 1UL << localIndex;
            }
        }

        return result;
    }
}
