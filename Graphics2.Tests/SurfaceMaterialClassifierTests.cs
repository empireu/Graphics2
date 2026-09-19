using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.World;

namespace Graphics2.Tests;

public class SurfaceMaterialClassifierTests
{
    private static SurfaceMaterialClassifier CreateClassifier(float snowLine = 16.0f)
    {
        return new SurfaceMaterialClassifier(snowLine);
    }

    [Fact]
    public void Classify_PutsSnowOnFlatGroundAboveTheLine()
    {
        var classifier = CreateClassifier();

        Assert.Equal(SurfaceMaterial.Snow, classifier.Classify(1.0f, 24.0f));
    }

    [Fact]
    public void Classify_PutsIceOnFlatGroundBelowTheLine()
    {
        var classifier = CreateClassifier();

        Assert.Equal(SurfaceMaterial.Ice, classifier.Classify(1.0f, 8.0f));
    }

    [Fact]
    public void Classify_PutsRockOnVerticalFacesAtAnyHeight()
    {
        var classifier = CreateClassifier();

        Assert.Equal(SurfaceMaterial.Rock, classifier.Classify(0.0f, 24.0f));
        Assert.Equal(SurfaceMaterial.Rock, classifier.Classify(0.0f, 8.0f));
    }

    [Fact]
    public void Classify_PutsRockOnFacesTooSteepToHoldSnow()
    {
        var classifier = CreateClassifier();

        Assert.Equal(SurfaceMaterial.Rock, classifier.Classify(0.6f, 24.0f));
    }

    [Fact]
    public void Score_IsBetweenZeroAndOne()
    {
        var classifier = CreateClassifier();

        for (var slope = 0.0f; slope <= 1.0f; slope += 0.05f)
        {
            for (var height = 0.0f; height <= 40.0f; height += 2.0f)
            {
                var scores = classifier.Score(slope, height);

                Assert.InRange(scores.Snow, 0.0f, 1.0f);
                Assert.InRange(scores.Ice, 0.0f, 1.0f);
                Assert.InRange(scores.Rock, 0.0f, 1.0f);
                Assert.False(float.IsNaN(scores.Snow) || float.IsNaN(scores.Ice) || float.IsNaN(scores.Rock));
            }
        }
    }

    [Fact]
    public void Classify_AcceptsCrossedSlopeEdges()
    {
        var classifier = new SurfaceMaterialClassifier(16.0f, 3.0f, snowSlopeMin: 0.95f, snowSlopeMax: 0.80f, rockSlopeMin: 0.70f, rockSlopeMax: 0.45f);

        Assert.Equal(SurfaceMaterial.Snow, classifier.Classify(1.0f, 24.0f));
        Assert.Equal(SurfaceMaterial.Ice, classifier.Classify(1.0f, 8.0f));
    }

    [Fact]
    public void Generate_MarksAirAsNullAndClassifiesSolidSamples()
    {
        // A flat surface at y = 16, with no relief, so every solid sample has a slope of 1:
        var generator = new FbmTerrainGenerator(1, 16.0f, 16.0f, 0.0f, new SurfaceMaterialClassifier(snowLine: 30.0f));

        var content = Memory3D<byte>.Allocate(4, 8, 4);
        var material = Memory3D<byte>.Allocate(4, 8, 4);

        generator.Generate(new Vector3i(0, 12, 0), new Vector3i(3, 19, 3), content.Span, material.Span);

        // Above and at the surface is air, which carries no material:
        Assert.Equal(SurfaceMaterial.Null, material.Span[0, 7, 0]);
        Assert.Equal(SurfaceMaterial.Null, material.Span[0, 4, 0]);

        // The solid samples below it are flat and under the snow line, so they read as ice:
        Assert.Equal(SurfaceMaterial.Ice, material.Span[0, 3, 0]);
        Assert.Equal(SurfaceMaterial.Ice, material.Span[3, 0, 3]);
    }
}
