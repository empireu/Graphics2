using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinterGame.World.Generation;
using WinterGame.World.Generation.GraphProgram;

namespace WinterGame.Tests;

public class DensityGraphTests
{
    [Fact]
    public void Compiler_LeavesThePositionSlotsReserved()
    {
        var builder = new DensityGraphBuilder(1);
        var graph = builder.Compile(builder.X);

        Assert.Empty(graph.SampleProgram);
        Assert.Equal(DensityGraph.PositionSlotX, graph.SampleRoots[0]);
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
        Assert.Equal(instruction.Output, graph.SampleRoots[0]);
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
        Assert.Equal(corner.Output, graph.BoundaryRoot);
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

    [Fact]
    public void Compiler_CompilesARootSetIntoOneSharedProgram()
    {
        var builder = new DensityGraphBuilder(1);
        var hills = builder.Noise2D("hills", frequency: 0.25f);
        var graph = builder.Compile([builder.Add(hills, hills), builder.Mul(hills, builder.Constant(3.0f))]);

        Assert.Equal(2, graph.RootCount);

        // The shared noise is emitted once, so the two roots are drawn from one program:
        var noiseCount = 0;

        foreach (var instruction in graph.SampleProgram)
        {
            if (instruction.Op == DensityOp.Noise2D)
            {
                noiseCount++;
            }
        }

        Assert.Equal(1, noiseCount);
        Assert.NotEqual(graph.SampleRoots[0], graph.SampleRoots[1]);
    }

    [Fact]
    public void Compiler_PutsARootBelowTheBoundaryInTheCornerProgram()
    {
        var builder = new DensityGraphBuilder(1);
        var shape = builder.Mul(builder.Noise2D("shape", frequency: 0.25f), builder.Constant(8.0f));
        var depth = builder.Interpolated(builder.Add(shape, builder.Mul(builder.Y, builder.Constant(-1.0f))));
        var graph = builder.Compile([shape, depth]);

        Assert.True(graph.RootIsCorner[0]);
        Assert.False(graph.RootIsCorner[1]);
        Assert.NotEqual(DensityInstruction.NoSlot, graph.CornerRoots[0]);
        Assert.Equal(DensityInstruction.NoSlot, graph.SampleRoots[0]);
        Assert.Equal(DensityInstruction.NoSlot, graph.CornerRoots[1]);
    }

    [Fact]
    public void Compiler_HoistsWhatDoesNotDependOnTheHeight()
    {
        var builder = new DensityGraphBuilder(1);
        var caves = builder.Noise3D("caves", frequency: 0.25f);
        var depth = builder.Add(builder.Constant(4.0f), builder.Mul(builder.Y, builder.Constant(-1.0f)));
        var hills = builder.Noise2D("hills", frequency: 0.25f);
        var graph = builder.Compile([builder.Add(caves, depth), builder.Add(hills, hills)]);

        // Both parts keep the author order, and the constants count as hoisted:
        Assert.Equal(
            [DensityOp.Constant, DensityOp.Constant, DensityOp.Noise2D, DensityOp.Add, DensityOp.Noise3D, DensityOp.Mul, DensityOp.Add, DensityOp.Add],
            graph.SampleProgram.Select(instruction => instruction.Op)
        );

        Assert.Equal(4, graph.SampleColumnLength);
    }

    [Fact]
    public void Compiler_FoldsASelectWithAConstantCondition()
    {
        var builder = new DensityGraphBuilder(1);
        var root = builder.Select(builder.Constant(1.0f), builder.Constant(2.0f), builder.Constant(3.0f));
        var graph = builder.Compile(root);

        var instruction = Assert.Single(graph.SampleProgram);

        Assert.Equal(DensityOp.Constant, instruction.Op);
        Assert.Equal(2.0f, graph.Constants[instruction.Payload]);
    }

    private static DensityGraph TwoNoiseGraph(int seed)
    {
        var builder = new DensityGraphBuilder(seed);

        return builder.Compile(builder.Add(builder.Noise2D("continents"), builder.Noise2D("hills")));
    }
}
