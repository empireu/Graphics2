using System.Numerics;
using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.World;
using WinterGame.World.Generation;
using WinterGame.World.Generation.GraphProgram;
using WinterGame.World.Level;

namespace WinterGame.Simulation;

/// <summary>
///     A temporary sphere brush for trying out edits to the level. It digs or fills where the player's view first meets solid ground.
/// </summary>
internal sealed class DebugBrush
{
    /// <summary>
    ///     The sphere's radius in metres.
    /// </summary>
    public const float Radius = RadiusInSamples * WorldConstants.VoxelSize;

    private const int RadiusInSamples = 4;

    /// <summary>
    ///     How far along the view the brush reaches, in metres.
    /// </summary>
    private const float Reach = 8.0f;

    private const byte FillMaterial = SurfaceMaterial.Rock;

    /// <summary>
    ///     Samples from the centre's sample to each side of the box, which covers the radius and the one sample ramp outside it.
    /// </summary>
    private const int BoxExtent = RadiusInSamples + 2;

    private readonly Memory3D<byte> _content;
    private readonly Memory3D<byte> _material;

    public DebugBrush()
    {
        const int boxSize = 2 * BoxExtent + 2;

        _content = Memory3D<byte>.Allocate(boxSize, boxSize, boxSize);
        _material = Memory3D<byte>.Allocate(boxSize, boxSize, boxSize);
    }

    /// <summary>
    ///     Where the view met solid ground in the last tick, in metres, or null when it met none within reach.
    /// </summary>
    public Vector3? Target { get; private set; }

    /// <summary>
    ///     Digs and then fills when asked, each aimed along <paramref name="direction"/> from <paramref name="eye"/> in metres, then aims again for <see cref="Target"/>.
    /// </summary>
    public void Tick(WorldLevel level, Vector3 eye, Vector3 direction, bool dig, bool fill)
    {
        if (dig && TryPick(level, eye, direction, out var digCenter))
        {
            Apply(level, digCenter, false);
        }

        if (fill && TryPick(level, eye, direction, out var fillCenter))
        {
            Apply(level, fillCenter, true);
        }

        Target = TryPick(level, eye, direction, out var target) ? target : null;
    }

    /// <summary>
    ///     Walks the samples along the ray and returns where it enters the first solid one, in metres.
    ///     The walk gives up past the reach and at a section that is not loaded, which would read as air.
    /// </summary>
    private static bool TryPick(WorldLevel level, Vector3 eye, Vector3 direction, out Vector3 hit)
    {
        hit = default;

        // A sample owns the points nearer to it than to any other sample, so the walk runs on the lattice shifted by half a sample:
        var origin = eye / WorldConstants.VoxelSize + new Vector3(0.5f);
        var sample = new Vector3i((int)MathF.Floor(origin.X), (int)MathF.Floor(origin.Y), (int)MathF.Floor(origin.Z));

        var stepX = Math.Sign(direction.X);
        var stepY = Math.Sign(direction.Y);
        var stepZ = Math.Sign(direction.Z);

        var deltaX = 1.0f / MathF.Abs(direction.X);
        var deltaY = 1.0f / MathF.Abs(direction.Y);
        var deltaZ = 1.0f / MathF.Abs(direction.Z);

        var nextX = GetFirstCrossing(origin.X, direction.X, sample.X);
        var nextY = GetFirstCrossing(origin.Y, direction.Y, sample.Y);
        var nextZ = GetFirstCrossing(origin.Z, direction.Z, sample.Z);

        var reach = Reach / WorldConstants.VoxelSize;
        var distance = 0.0f;
        var accessor = new FastLevelAccessor(level);

        while (distance <= reach)
        {
            if (!accessor.ReadPoint(sample, out var content, out _))
            {
                return false;
            }

            if (content > WorldConstants.ContentIsoLevel)
            {
                hit = eye + direction * (distance * WorldConstants.VoxelSize);
                return true;
            }

            if (nextX <= nextY && nextX <= nextZ)
            {
                sample.X += stepX;
                distance = nextX;
                nextX += deltaX;
            }
            else if (nextY <= nextZ)
            {
                sample.Y += stepY;
                distance = nextY;
                nextY += deltaY;
            }
            else
            {
                sample.Z += stepZ;
                distance = nextZ;
                nextZ += deltaZ;
            }
        }

        return false;
    }

    /// <summary>
    ///     The distance along the ray to where it leaves <paramref name="sample"/>'s cell on one axis, infinite when the ray runs parallel to that axis's faces.
    /// </summary>
    private static float GetFirstCrossing(float origin, float direction, int sample)
    {
        if (direction > 0.0f)
        {
            return (sample + 1 - origin) / direction;
        }

        if (direction < 0.0f)
        {
            return (sample - origin) / direction;
        }

        return float.PositiveInfinity;
    }

    /// <summary>
    ///     Digs or fills the sphere around <paramref name="center"/>, in metres, with the generator's ramp at its surface.
    ///     A dig leaves the null material wherever a sample turns to air, and a fill writes its material only where a sample turns solid, so the ground keeps its own.
    /// </summary>
    private void Apply(WorldLevel level, Vector3 center, bool fill)
    {
        var centerInSamples = center / WorldConstants.VoxelSize;
        var centerSample = new Vector3i((int)MathF.Floor(centerInSamples.X), (int)MathF.Floor(centerInSamples.Y), (int)MathF.Floor(centerInSamples.Z));
        var min = centerSample - BoxExtent;
        var maxInclusive = centerSample + (BoxExtent + 1);
        var content = _content.Span;
        var material = _material.Span;

        // The write would refuse a box that reaches a section that is not loaded anyway:
        if (!level.ReadRange(min, maxInclusive, content, material))
        {
            return;
        }

        for (var z = 0; z < content.SizeZ; z++)
        {
            for (var y = 0; y < content.SizeY; y++)
            {
                for (var x = 0; x < content.SizeX; x++)
                {
                    var offset = new Vector3(min.X + x, min.Y + y, min.Z + z) - centerInSamples;

                    // Positive outside the sphere, in metres:
                    var distance = (offset.Length() - RadiusInSamples) * WorldConstants.VoxelSize;

                    ref var sampleContent = ref content[x, y, z];
                    ref var sampleMaterial = ref material[x, y, z];

                    if (fill)
                    {
                        var brushContent = DensitySampler.ContentFromDensity(-distance, 0);

                        if (brushContent <= sampleContent)
                        {
                            continue;
                        }

                        if (sampleContent <= WorldConstants.ContentIsoLevel && brushContent > WorldConstants.ContentIsoLevel)
                        {
                            sampleMaterial = FillMaterial;
                        }

                        sampleContent = brushContent;
                    }
                    else
                    {
                        var brushContent = DensitySampler.ContentFromDensity(distance, 0);

                        if (brushContent >= sampleContent)
                        {
                            continue;
                        }

                        sampleContent = brushContent;

                        if (brushContent <= WorldConstants.ContentIsoLevel)
                        {
                            sampleMaterial = SurfaceMaterial.Null;
                        }
                    }
                }
            }
        }

        level.WriteRange(min, maxInclusive, content, material);
    }
}
