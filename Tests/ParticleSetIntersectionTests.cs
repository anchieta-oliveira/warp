using Warp;
using Warp.Sociology;
using Xunit;

namespace Tests;

public class ParticleSetIntersectionTests
{
    [Fact]
    public void MatchSeparatesSourcesAndUsesOptimalAssignments()
    {
        Particle oldA = ParticleAt(0, "a");
        Particle oldB = ParticleAt(10, "a");
        Particle oldOther = ParticleAt(0, "b");
        Particle newA = ParticleAt(9, "a");
        Particle newB = ParticleAt(1, "a");
        Particle newOther = ParticleAt(2, "c");

        ParticleSetIntersection.Result result = ParticleSetIntersection.Match([oldA, oldB, oldOther], [newA, newB, newOther]);

        Assert.Equal(1f, result.OldDistances[0]);
        Assert.Equal(1f, result.OldDistances[1]);
        Assert.Equal(-1f, result.OldDistances[2]);
        Assert.Equal(-1f, result.NewDistances[2]);
    }

    [Fact]
    public void SelectPreservesOldObjectsAndClassifiesByTolerance()
    {
        Particle oldMatched = ParticleAt(0, "a", trajectory: true);
        Particle oldOnly = ParticleAt(100, "a");
        Particle newMatched = ParticleAt(2, "a");
        Particle newOnly = ParticleAt(200, "a");
        Particle[] oldParticles = [oldMatched, oldOnly];
        Particle[] newParticles = [newMatched, newOnly];
        ParticleSetIntersection.Result match = ParticleSetIntersection.Match(oldParticles, newParticles);

        Particle[] selected = ParticleSetIntersection.Select(oldParticles, newParticles, match, 5, true, true, true);

        Assert.Equal(3, selected.Length);
        Assert.Same(oldMatched, selected[0]);
        Assert.Same(oldOnly, selected[1]);
        Assert.Same(newOnly, selected[2]);
        Assert.Equal(2, oldMatched.Coordinates.Length);
    }

    private static Particle ParticleAt(float x, string hash, bool trajectory = false) => new(
        trajectory ? [new float3(x, 0, 0), new float3(x + 1, 0, 0)] : [new float3(x, 0, 0)],
        [new float3()], 0, hash, hash);
}
