using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Warp.Tools;
using ZLinq;

namespace Warp;

public static class RelionParticleSeriesExport
{
    public static int[] GetVisibleTiltIndices(IReadOnlyList<int> usedTilts,
                                              IReadOnlyList<bool> visibility)
    {
        if (usedTilts.Count != visibility.Count)
            throw new ArgumentException("Used-tilt and visibility counts must match.");

        return usedTilts.Where((_, i) => visibility[i]).ToArray();
    }

    public static int3 GetVirtualTomogramDimensions(float3 dimensionsPhysical,
                                                     float particlePixelSize)
    {
        if (particlePixelSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(particlePixelSize));

        return new int3(
            Math.Max(1, (int)Math.Round(dimensionsPhysical.X / particlePixelSize)),
            Math.Max(1, (int)Math.Round(dimensionsPhysical.Y / particlePixelSize)),
            Math.Max(1, (int)Math.Round(dimensionsPhysical.Z / particlePixelSize)));
    }

    public static float GetRelionHand(bool areAnglesInverted)
    {
        return areAnglesInverted ? 1f : -1f;
    }

    public static decimal GetPhaseShiftDegrees(decimal phaseShiftPi)
    {
        return phaseShiftPi * 180M;
    }

    public static bool ShouldExcludeParticle(string visibleFrames, int maxMissingTilts)
    {
        int[] visibility = visibleFrames.Trim('[', ']').Split(',').Select(int.Parse).ToArray();
        int visible = visibility.Count(v => v != 0);
        return visible == 0 || visibility.Length - visible > maxMissingTilts;
    }

    /// <summary>Writes the RELION files shared by all 2D particle-series exporters.</summary>
    public static void WriteOutputFiles(Dictionary<string, Star> tables, string particleStarPath,
                                        int maxMissingTilts, string pathsRelativeTo)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(particleStarPath));
        Directory.CreateDirectory(directory);

        Star general = new StarParameters(new[] { "rlnTomoSubTomosAre2DStacks" }, new[] { "1" });
        Star optics = new(tables.Where(p => p.Key.EndsWith("_optics"))
                              .ToDictionary(p => p.Key, p => p.Value).Values.ToArray());
        Star particles = new(tables.Where(p => p.Key.EndsWith("_particles"))
                                 .ToDictionary(p => p.Key, p => p.Value).Values.ToArray());
        particles.RemoveRowsWhere("rlnTomoVisibleFrames",
            value => ShouldExcludeParticle(value, maxMissingTilts));
        Star.SaveMultitable(particleStarPath, new Dictionary<string, Star>
        {
            { "general", general }, { "optics", optics }, { "particles", particles }
        });

        Star tomogramsGlobal = new(tables.Where(p => p.Key.EndsWith("_tomograms_global"))
                                       .ToDictionary(p => p.Key, p => p.Value).Values.ToArray());
        string dummyPath = Path.Combine(directory, "dummy_tiltseries.mrc");
        using (Image dummy = new(new int3(2, 2, 2), false, false))
            dummy.WriteMRC16b(dummyPath);
        tomogramsGlobal.ModifyAllValuesInColumn("rlnTomoTiltSeriesName",
            _ => Helper.MakePathRelativeTo(dummyPath, pathsRelativeTo));

        const string suffix = "_tomograms_tiltseries";
        Dictionary<string, Star> tomograms = new() { { "global", tomogramsGlobal } };
        foreach (var pair in tables.Where(p => p.Key.EndsWith(suffix)))
            tomograms.Add(pair.Key[..^suffix.Length] + ".tomostar", pair.Value);
        string tomogramsPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(particleStarPath) + "_tomograms.star");
        Star.SaveMultitable(tomogramsPath, tomograms);

        string particleFile = Helper.MakePathRelativeTo(particleStarPath, pathsRelativeTo);
        string tomogramsFile = Helper.MakePathRelativeTo(tomogramsPath, pathsRelativeTo);
        File.WriteAllText(Path.Combine(directory, Path.GetFileNameWithoutExtension(particleStarPath) + "_optimisation_set.star"),
            "data_\n\n" + $"_rlnTomoParticlesFile   {particleFile}\n" +
            $"_rlnTomoTomogramsFile   {tomogramsFile}\n");
    }
}
