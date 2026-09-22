using Graphics2.Mathematics.Interpolation;

namespace Graphics2.Tests;

public class CubicSplineTests
{
    [Fact]
    public void Evaluate_PassesThroughTheControlPoints()
    {
        var spline = new CubicSpline([0.0f, 1.0f, 4.0f], [2.0f, -1.0f, 3.0f]);

        Assert.Equal(2.0f, spline.Evaluate(0.0f));
        Assert.Equal(-1.0f, spline.Evaluate(1.0f));
        Assert.Equal(3.0f, spline.Evaluate(4.0f));
    }

    [Fact]
    public void Evaluate_ClampsOutsideTheControlRange()
    {
        var spline = new CubicSpline([0.0f, 1.0f], [2.0f, 5.0f]);

        Assert.Equal(2.0f, spline.Evaluate(-100.0f));
        Assert.Equal(5.0f, spline.Evaluate(100.0f));
    }

    [Fact]
    public void Evaluate_IsExactForACollinearSetOfPoints()
    {
        var spline = new CubicSpline([0.0f, 1.0f, 2.0f], [0.0f, 1.0f, 2.0f]);

        Assert.Equal(0.5f, spline.Evaluate(0.5f));
        Assert.Equal(1.5f, spline.Evaluate(1.5f));
    }

    [Fact]
    public void Evaluate_StaysBetweenItsNeighbours()
    {
        var spline = new CubicSpline([0.0f, 1.0f, 2.0f], [0.0f, 1.0f, 0.0f]);

        for (var index = 0; index <= 20; index++)
        {
            var input = index / 20.0f;

            Assert.InRange(spline.Evaluate(input), 0.0f, 1.0f);
        }
    }

    [Fact]
    public void Constructor_RejectsControlPointsThatDoNotIncrease()
    {
        Assert.Throws<ArgumentException>(() => new CubicSpline([0.0f, 0.0f], [1.0f, 2.0f]));
        Assert.Throws<ArgumentException>(() => new CubicSpline([1.0f, 0.0f], [1.0f, 2.0f]));
    }

    [Fact]
    public void Constructor_RejectsFewerThanTwoPoints()
    {
        Assert.Throws<ArgumentException>(() => new CubicSpline([0.0f], [1.0f]));
        Assert.Throws<ArgumentException>(() => new CubicSpline([0.0f, 1.0f], [1.0f]));
    }

    [Fact]
    public void Equals_ComparesTheControlPoints()
    {
        var spline = new CubicSpline([0.0f, 1.0f], [2.0f, 3.0f]);

        Assert.Equal(spline, new CubicSpline([0.0f, 1.0f], [2.0f, 3.0f]));
        Assert.NotEqual(spline, new CubicSpline([0.0f, 1.0f], [2.0f, 4.0f]));
    }
}
