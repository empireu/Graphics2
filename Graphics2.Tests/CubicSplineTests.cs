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
    public void Evaluate_StaysBetweenItsNeighbors()
    {
        var spline = new CubicSpline([0.0f, 1.0f, 2.0f], [0.0f, 1.0f, 0.0f]);

        for (var index = 0; index <= 20; index++)
        {
            var input = index / 20.0f;

            Assert.InRange(spline.Evaluate(input), 0.0f, 1.0f);
        }
    }

    [Fact]
    public void Maximum_IsTheHighestControlValueOfAMonotoneCurve()
    {
        var spline = new CubicSpline([-1.0f, -0.35f, 0.0f, 0.4f, 1.0f], [-8.0f, 2.0f, 12.0f, 40.0f, 70.0f]);

        Assert.Equal(70.0f, spline.Maximum());
        Assert.True(DenseMaximum(spline, -1.0f, 1.0f) <= 70.0f);
    }

    [Fact]
    public void Maximum_FindsAnOvershootBetweenControlPoints()
    {
        // The tangent at 2 rises towards the plateau, so the last segment overshoots 1:
        var spline = new CubicSpline([0.0f, 1.0f, 2.0f, 3.0f], [0.0f, 0.0f, 1.0f, 1.0f]);
        var dense = DenseMaximum(spline, 0.0f, 3.0f);

        Assert.True(dense > 1.05f);
        Assert.InRange(spline.Maximum(), dense - 1.0e-5f, dense + 1.0e-5f);
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

    private static float DenseMaximum(CubicSpline spline, float from, float to)
    {
        var maximum = float.NegativeInfinity;

        for (var index = 0; index <= 100000; index++)
        {
            maximum = Math.Max(maximum, spline.Evaluate(from + (to - from) * index / 100000.0f));
        }

        return maximum;
    }
}
