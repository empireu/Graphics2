using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinterGame.World.Generation;

namespace WinterGame.Tests;

public class DensityGraphTests
{
    [Fact]
    public void Compiler_LeavesThePositionSlotsReserved()
    {
        var builder = new DensityGraphBuilder(1);
        var graph = builder.Compile(builder.X);

        Assert.Empty(graph.SampleProgram);
        Assert.Equal(DensityGraph.PositionSlotX, graph.SampleRoot);
        Assert.Equal(DensityGraph.FirstProgramSlot, graph.SampleSlotCount);
    }

    [Fact]
    public void Compiler_FoldsConstantsIntoOneInstruction()
    {
        var builder = new DensityGraphBuilder(1);
        var root = builder.Add(builder.Constant(2.0f), builder.Constant(3.0f));
        var graph = builder.Compile(root);

        var instruction = Assert.Single(graph.SampleProgram);

        Assert.Equal(DensityOp.Constant, instruction.Op);
        Assert.Equal(5.0f, graph.Constants[instruction.Payload]);
        Assert.Equal(instruction.Output, graph.SampleRoot);
    }

    [Fact]
    public void Compiler_DropsAStageMultipliedByZero()
    {
        var builder = new DensityGraphBuilder(1);
        var hills = builder.Noise2D("hills", frequency: 0.25f);
        var root = builder.Mul(hills, builder.Constant(0.0f));
        var graph = builder.Compile(root);

        var instruction = Assert.Single(graph.SampleProgram);

        Assert.Equal(DensityOp.Constant, instruction.Op);
        Assert.Equal(0.0f, graph.Constants[instruction.Payload]);
        Assert.Empty(graph.Noises);
    }

    [Fact]
    public void Compiler_EmitsASharedNodeOnce()
    {
        var builder = new DensityGraphBuilder(1);
        var hills = builder.Noise2D("hills", frequency: 0.25f);
        var graph = builder.Compile(builder.Add(hills, hills));

        Assert.Equal(2, graph.SampleProgram.Length);

        var noise = graph.SampleProgram[0];
        var add = graph.SampleProgram[1];

        Assert.Equal(DensityOp.Noise2D, noise.Op);
        Assert.Equal(DensityOp.Add, add.Op);
        Assert.Equal(noise.Output, add.Input0);
        Assert.Equal(noise.Output, add.Input1);
    }

    [Fact]
    public void Compiler_SeedsEachNoiseFromTheBuilderSeedAndTheName()
    {
        var first = TwoNoiseGraph(7);
        var second = TwoNoiseGraph(7);
        var other = TwoNoiseGraph(8);

        Assert.Equal(first.Noises[0].Seed, second.Noises[0].Seed);
        Assert.Equal(first.Noises[1].Seed, second.Noises[1].Seed);
        Assert.NotEqual(first.Noises[0].Seed, first.Noises[1].Seed);
        Assert.NotEqual(first.Noises[0].Seed, other.Noises[0].Seed);
    }

    [Fact]
    public void Compiler_ReservesTheFractionAndCornerSlotsWithABoundary()
    {
        var builder = new DensityGraphBuilder(1);
        var graph = builder.Compile(builder.Interpolated(builder.Constant(1.0f)));

        Assert.True(graph.HasInterpolation);

        var interpolated = Assert.Single(graph.SampleProgram);
        var corner = Assert.Single(graph.CornerProgram);

        Assert.Equal(DensityOp.Interpolated, interpolated.Op);
        Assert.Equal(DensityGraph.FirstInterpolatedSampleSlot, interpolated.Output);
        Assert.Equal(DensityGraph.FirstInterpolatedSampleSlot + 1, graph.SampleSlotCount);
        Assert.Equal(DensityOp.Constant, corner.Op);
        Assert.Equal(corner.Output, graph.CornerRoot);
        Assert.Equal(DensityGraph.FirstProgramSlot + 1, graph.CornerSlotCount);
    }

    [Fact]
    public void Builder_RejectsADuplicateNoiseName()
    {
        var builder = new DensityGraphBuilder(1);

        Assert.Throws<ArgumentException>(
            () =>
            {
                builder.Noise2D("hills");
                builder.Noise2D("hills");
            }
        );
    }

    [Fact]
    public void Builder_RejectsASecondInterpolationBoundary()
    {
        var builder = new DensityGraphBuilder(1);

        Assert.Throws<InvalidOperationException>(
            () =>
            {
                var value = builder.Constant(1.0f);
                builder.Interpolated(value);
                builder.Interpolated(value);
            }
        );
    }

    [Fact]
    public void Builder_RejectsANodeFromAnotherBuilder()
    {
        var first = new DensityGraphBuilder(1);
        var second = new DensityGraphBuilder(1);

        Assert.Throws<ArgumentException>(() => first.Add(first.Constant(1.0f), second.Constant(2.0f)));
    }

    [Fact]
    public void Builder_RejectsANonFiniteConstant()
    {
        var builder = new DensityGraphBuilder(1);

        Assert.Throws<ArgumentException>(() => builder.Constant(float.NaN));
    }

    [Fact]
    public void Compiler_RejectsACellSizeBelowOne()
    {
        var builder = new DensityGraphBuilder(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Compile(builder.X, cellX: 0));
    }

    [Fact]
    public void Instruction_Layout()
    {
        Assert.Equal(24, Unsafe.SizeOf<DensityInstruction>());
        Assert.Equal(0, (int)Marshal.OffsetOf<DensityInstruction>(nameof(DensityInstruction.Op)));
        Assert.Equal(4, (int)Marshal.OffsetOf<DensityInstruction>(nameof(DensityInstruction.Output)));
        Assert.Equal(8, (int)Marshal.OffsetOf<DensityInstruction>(nameof(DensityInstruction.Input0)));
        Assert.Equal(12, (int)Marshal.OffsetOf<DensityInstruction>(nameof(DensityInstruction.Input1)));
        Assert.Equal(16, (int)Marshal.OffsetOf<DensityInstruction>(nameof(DensityInstruction.Input2)));
        Assert.Equal(20, (int)Marshal.OffsetOf<DensityInstruction>(nameof(DensityInstruction.Payload)));
    }

    private static DensityGraph TwoNoiseGraph(int seed)
    {
        var builder = new DensityGraphBuilder(seed);

        return builder.Compile(builder.Add(builder.Noise2D("continents"), builder.Noise2D("hills")));
    }
}
