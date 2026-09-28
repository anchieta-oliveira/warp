using Warp.Sociology;
using Warp.Tools;
using Xunit;

namespace Tests;

public class SpeciesSubtomoExporterTests
{
    [Fact]
    public void InterpolationStepsFollowDoseAndHandleConstantDose()
    {
        Assert.Equal(new[] { 0f, 0.5f, 1f },
                     SpeciesSubtomoExporter.GetInterpolationSteps(new[] { 2f, 4f, 6f }));
        Assert.Equal(new[] { 0f, 0f, 0f },
                     SpeciesSubtomoExporter.GetInterpolationSteps(new[] { 5f, 5f, 5f }));
    }

    [Fact]
    public void TrajectoryArraysAreParticleMajorAndPreserveTiltVariation()
    {
        Particle first = new(
            new[] { new float3(0, 0, 0), new float3(10, 0, 0), new float3(20, 0, 0) },
            new[] { new float3(0, 0, 0), new float3(10, 0, 0), new float3(20, 0, 0) },
            0, "series_a", "hash_a");
        Particle second = new(
            new[] { new float3(100, 0, 0), new float3(110, 0, 0), new float3(120, 0, 0) },
            new[] { new float3(30, 0, 0), new float3(40, 0, 0), new float3(50, 0, 0) },
            1, "series_a", "hash_a");

        var trajectories = SpeciesSubtomoExporter.BuildTrajectoryArrays(
            new[] { first, second }, new[] { 0f, 0.5f, 1f }, prerotate: true, new float3(0));

        Assert.Equal(6, trajectories.Positions.Length);
        Assert.Equal(new float3(0, 0, 0), trajectories.Positions[0]);
        Assert.Equal(new float3(10, 0, 0), trajectories.Positions[1]);
        Assert.Equal(new float3(20, 0, 0), trajectories.Positions[2]);
        Assert.Equal(new float3(100, 0, 0), trajectories.Positions[3]);
        Assert.Equal(new float3(110, 0, 0), trajectories.Positions[4]);
        Assert.Equal(new float3(120, 0, 0), trajectories.Positions[5]);
        Assert.NotEqual(trajectories.Positions[0], trajectories.Positions[1]);
        Assert.NotEqual(trajectories.Angles[3], trajectories.Angles[4]);
    }
}
