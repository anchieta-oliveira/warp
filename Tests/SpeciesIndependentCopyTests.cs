using System;
using System.Collections.Generic;
using System.IO;
using Warp;
using Warp.Sociology;
using Xunit;

namespace Tests;

public class SpeciesIndependentCopyTests : IDisposable
{
    private readonly string Root = Path.Combine(Path.GetTempPath(), "warp-species-copy-" + Guid.NewGuid());

    [Fact]
    public void IndependentCopyResetsIdentityAndCopiesCurrentArtifacts()
    {
        string originalPath = Path.Combine(Root, "original", "original.species");
        Species original = new Species(null, null, null)
        {
            Name = "Original",
            Path = originalPath,
            PixelSize = 2.5m,
            Symmetry = "D7",
            Version = "old-version",
            PreviousVersion = "older-version",
            UsedDataSources = new Dictionary<Guid, string> { [Guid.NewGuid()] = "source-version" }
        };
        original.ReplaceParticles([new Particle([new float3(1, 2, 3)], [new float3(4, 5, 6)], 1, "source", "hash", 0.8f)]);
        Directory.CreateDirectory(original.FolderPath);
        File.WriteAllText(original.PathHalfMap1, "half-map");
        File.WriteAllText(original.PathNoiseNet, "denoiser");
        string oldVersionFolder = Path.Combine(original.FolderPath, "versions", "old");
        Directory.CreateDirectory(oldVersionFolder);
        File.WriteAllText(Path.Combine(oldVersionFolder, "ignored.txt"), "old version");

        Species copy = original.CreateIndependentTopLevelCopy("Selection", Path.Combine(Root, "selection", "Selection.species"), original.Particles);

        Assert.NotEqual(original.GUID, copy.GUID);
        Assert.Equal("", copy.Version);
        Assert.Equal("", copy.PreviousVersion);
        Assert.Null(copy.Parent);
        Assert.Empty(copy.Children);
        Assert.Equal(original.PixelSize, copy.PixelSize);
        Assert.Equal(original.Symmetry, copy.Symmetry);
        Assert.Equal(original.UsedDataSources, copy.UsedDataSources);
        Assert.NotSame(original.UsedDataSources, copy.UsedDataSources);
        Assert.NotSame(original.Particles[0], copy.Particles[0]);
        Assert.Equal(original.Particles[0].FOM, copy.Particles[0].FOM);
        Assert.Equal("half-map", File.ReadAllText(copy.PathHalfMap1));
        Assert.Equal("denoiser", File.ReadAllText(copy.PathNoiseNet));
        Assert.False(Directory.Exists(Path.Combine(copy.FolderPath, "versions")));
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, true);
    }
}
