using System.Numerics;
using Graphics2.Mathematics;

namespace Graphics2.Tests;

public class FrustumTests
{
    /// <summary>
    ///     A camera at the origin looking along +Z, built as the game's camera builds it, with the projection's Y flipped for Vulkan.
    /// </summary>
    private static Frustum CreateCamera()
    {
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3.0f, 16.0f / 9.0f, 0.1f, 100.0f);
        projection.M22 = -projection.M22;

        return new Frustum(view * projection);
    }

    private static bool IntersectsCube(Frustum frustum, Vector3 center, float halfSize)
    {
        return frustum.Intersects(center - new Vector3(halfSize), center + new Vector3(halfSize));
    }

    [Fact]
    public void Perspective_KeepsABoxInFrontOfTheCamera()
    {
        var frustum = CreateCamera();

        Assert.True(IntersectsCube(frustum, new Vector3(0.0f, 0.0f, 10.0f), 0.5f));
        Assert.True(IntersectsCube(frustum, new Vector3(0.0f, 0.0f, 99.0f), 0.5f));
    }

    [Fact]
    public void Perspective_RejectsABoxBehindTheCameraOrPastTheFarPlane()
    {
        var frustum = CreateCamera();

        Assert.False(IntersectsCube(frustum, new Vector3(0.0f, 0.0f, -10.0f), 0.5f));
        Assert.False(IntersectsCube(frustum, new Vector3(0.0f, 0.0f, 110.0f), 0.5f));
    }

    [Fact]
    public void Perspective_RejectsABoxOutsideEachSide()
    {
        var frustum = CreateCamera();

        // At 10 m the half-height is 10 tan(30 deg), about 5.8 m, and the half-width 16/9 of it, about 10.3 m:
        Assert.False(IntersectsCube(frustum, new Vector3(-12.0f, 0.0f, 10.0f), 0.5f));
        Assert.False(IntersectsCube(frustum, new Vector3(12.0f, 0.0f, 10.0f), 0.5f));
        Assert.False(IntersectsCube(frustum, new Vector3(0.0f, -7.0f, 10.0f), 0.5f));
        Assert.False(IntersectsCube(frustum, new Vector3(0.0f, 7.0f, 10.0f), 0.5f));
    }

    [Fact]
    public void Perspective_KeepsABoxAcrossAnyPlane()
    {
        var frustum = CreateCamera();

        Assert.True(IntersectsCube(frustum, new Vector3(-10.5f, 0.0f, 10.0f), 0.5f));
        Assert.True(IntersectsCube(frustum, new Vector3(0.0f, 6.0f, 10.0f), 0.5f));
        Assert.True(IntersectsCube(frustum, new Vector3(0.0f, 0.0f, 100.0f), 0.5f));
        Assert.True(IntersectsCube(frustum, Vector3.Zero, 0.5f));
    }

    [Fact]
    public void Perspective_KeepsABoxHoldingTheWholeVolume()
    {
        Assert.True(IntersectsCube(CreateCamera(), Vector3.Zero, 1000.0f));
    }

    [Fact]
    public void Orthographic_KeepsItsBoxAndRejectsWhatLiesOutside()
    {
        // A light above the origin looking down, keeping 20 m across and depths 5 to 45 m below it:
        var view = Matrix4x4.CreateLookAt(new Vector3(0.0f, 50.0f, 0.0f), Vector3.Zero, Vector3.UnitZ);
        var projection = Matrix4x4.CreateOrthographicOffCenter(-10.0f, 10.0f, -10.0f, 10.0f, 5.0f, 45.0f);
        var frustum = new Frustum(view * projection);

        Assert.True(IntersectsCube(frustum, new Vector3(0.0f, 25.0f, 0.0f), 0.5f));
        Assert.True(IntersectsCube(frustum, new Vector3(9.0f, 10.0f, -9.0f), 0.5f));
        Assert.False(IntersectsCube(frustum, new Vector3(11.0f, 25.0f, 0.0f), 0.5f));
        Assert.False(IntersectsCube(frustum, new Vector3(0.0f, 25.0f, -11.0f), 0.5f));
        Assert.False(IntersectsCube(frustum, new Vector3(0.0f, 47.0f, 0.0f), 0.5f));
        Assert.False(IntersectsCube(frustum, new Vector3(0.0f, 3.0f, 0.0f), 0.5f));
    }
}
