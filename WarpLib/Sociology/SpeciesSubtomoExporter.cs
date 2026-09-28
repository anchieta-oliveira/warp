using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Warp.Tools;
using Warp.Workers;
using Warp.Workers.Queue;
using Warp.Workers.Scheduling;

namespace Warp.Sociology;

/// <summary>
/// Exports subtomograms from the refined pose trajectories stored by a Species.
/// This deliberately differs from WarpTools ts_export_particles: poses are sampled
/// from Particle.GetCoordinateSeries/GetAngleSeries, rather than replicated from a STAR.
/// </summary>
public sealed class SpeciesSubtomoExporter
{
    public sealed class Options
    {
        public string PopulationPath { get; init; }
        public string SpeciesSelector { get; init; }
        public string OutputStarPath { get; init; }
        public IReadOnlyList<int> Devices { get; init; }
        public int ProcessesPerDevice { get; init; } = 1;
        public string TaskDirectory { get; init; }
        public decimal? OutputPixelSize { get; init; }
        public int? BoxSize { get; init; }
        public int? Diameter { get; init; }
        public bool PrerotateParticles { get; init; }
        public bool LimitDose { get; init; }
        public int? NTilts { get; init; }
        public bool MakeSparse { get; init; }
        public float3 AdditionalShiftAngstrom { get; init; }
        public bool NormalizeInput { get; init; } = true;
        public bool NormalizeOutput { get; init; } = true;
        public bool Invert { get; init; } = true;
        public bool Overwrite { get; init; }
        public bool DryRun { get; init; }
    }

    public sealed class Result
    {
        public string PopulationPath { get; init; }
        public string SpeciesName { get; init; }
        public Guid SpeciesGuid { get; init; }
        public int ParticleCount { get; init; }
        public int SourceCount { get; init; }
        public string OutputStarPath { get; init; }
        public string MappingPath { get; init; }
    }

    private sealed class SourcePlan
    {
        public string Hash { get; init; }
        public DataSource Source { get; init; }
        public TiltSeries Series { get; init; }
        public Particle[] Particles { get; init; }
        public int[] SpeciesParticleIndices { get; init; }
        public ProcessingOptionsTomoSubReconstruction ExportOptions { get; init; }
        public float3[] Positions { get; init; }
        public float3[] Angles { get; init; }
        public string[] SubtomoPaths { get; init; }
        public string[] CtfPaths { get; init; }
    }

    private sealed class MappingFile
    {
        public string population { get; init; }
        public string species { get; init; }
        public string species_name { get; init; }
        public string species_path { get; init; }
        public string species_version { get; init; }
        public string warp_version { get; init; }
        public MappingParticle[] particles { get; init; }
    }

    private sealed class MappingParticle
    {
        public int export_index { get; init; }
        public string image_name { get; init; }
        public string ctf_image { get; init; }
        public string source_hash { get; init; }
        public string source_name { get; init; }
        public int species_particle_index { get; init; }
    }

    /// <summary>Returns GUI-equivalent dose-normalized sampling locations.</summary>
    public static float[] GetInterpolationSteps(float[] dose)
    {
        if (dose == null || dose.Length == 0)
            throw new ArgumentException("Tilt series has no dose values.", nameof(dose));

        float minDose = MathHelper.Min(dose);
        float maxDose = MathHelper.Max(dose);
        if (maxDose == minDose)
            return Helper.ArrayOfConstant(0f, dose.Length);

        return Helper.ArrayOfFunction(i => (dose[i] - minDose) / (maxDose - minDose), dose.Length);
    }

    /// <summary>
    /// Samples and flattens trajectories in the particle-major layout required by
    /// TiltSeries.ReconstructSubtomos: particle 0 / all tilts, particle 1 / all tilts.
    /// </summary>
    public static (float3[] Positions, float3[] Angles) BuildTrajectoryArrays(
        Particle[] particles, float[] interpolationSteps, bool prerotate, float3 additionalShift)
    {
        if (particles == null || particles.Length == 0)
            throw new ArgumentException("No particles were supplied.", nameof(particles));

        int nTilts = interpolationSteps.Length;
        float3[] positions = new float3[particles.Length * nTilts];
        float3[] angles = new float3[particles.Length * nTilts];

        for (int p = 0; p < particles.Length; p++)
        {
            float3[] particlePositions = particles[p].GetCoordinateSeries(interpolationSteps);
            float3[] particleAngles = particles[p].GetAngleSeries(interpolationSteps);

            if (additionalShift.Length() > 0)
            {
                Matrix3 r0 = Matrix3.Euler(particleAngles[0] * Helper.ToRad);
                float3 rotatedShift = r0 * additionalShift;
                for (int t = 0; t < particlePositions.Length; t++)
                    particlePositions[t] += rotatedShift;
            }

            if (!prerotate)
            {
                Matrix3 r0Inv = Matrix3.Euler(particleAngles[0] * Helper.ToRad).Transposed();
                for (int t = 0; t < particleAngles.Length; t++)
                    particleAngles[t] = Matrix3.EulerFromMatrix(r0Inv * Matrix3.Euler(particleAngles[t] * Helper.ToRad)) * Helper.ToDeg;
            }

            for (int t = 0; t < nTilts; t++)
            {
                positions[p * nTilts + t] = particlePositions[t];
                angles[p * nTilts + t] = particleAngles[t];
            }
        }

        return (positions, angles);
    }

    public Result Run(Options options, Action<string> status = null)
    {
        ValidateOptions(options);
        string populationPath = Path.GetFullPath(options.PopulationPath);
        string outputStarPath = Path.GetFullPath(options.OutputStarPath);
        string mappingPath = Path.Combine(Path.GetDirectoryName(outputStarPath)!,
                                          Path.GetFileNameWithoutExtension(outputStarPath) + "_mapping.json");

        status?.Invoke($"Loading population: {populationPath}");
        Population population = new Population(populationPath);
        Species species = ResolveSpecies(population, options.SpeciesSelector);
        Particle[] allParticles = species.Particles;
        if (allParticles.Length == 0)
            throw new InvalidOperationException($"Species '{species.Name}' contains no particles.");

        List<SourcePlan> plans = BuildPlans(population, species, allParticles, options, outputStarPath);
        if (plans.Count == 0)
            throw new InvalidOperationException($"Species '{species.Name}' has no particles associated with tilt series.");

        List<string> outputFiles = new() { outputStarPath, mappingPath };
        outputFiles.AddRange(plans.SelectMany(p => p.SubtomoPaths));
        outputFiles.AddRange(plans.SelectMany(p => p.CtfPaths));
        outputFiles.AddRange(plans.Select(p => Path.Combine(p.Series.SubtomoDir,
            $"{p.Series.RootName}{p.ExportOptions.Suffix}_{p.ExportOptions.BinnedPixelSizeMean:F2}A_average.mrc")));
        if (!options.Overwrite)
        {
            string[] existing = outputFiles.Where(File.Exists).Distinct().ToArray();
            if (existing.Length > 0)
                throw new IOException("Refusing to overwrite existing export files. Use --overwrite to replace them:\n" +
                                      string.Join("\n", existing));
        }

        Result result = new()
        {
            PopulationPath = populationPath,
            SpeciesName = species.Name,
            SpeciesGuid = species.GUID,
            ParticleCount = allParticles.Length,
            SourceCount = plans.Count,
            OutputStarPath = outputStarPath,
            MappingPath = mappingPath
        };

        status?.Invoke($"Species: {species.Name}");
        status?.Invoke($"Particles: {allParticles.Length}");
        status?.Invoke($"Sources: {plans.Count}");
        status?.Invoke($"Output angpix: {options.OutputPixelSize ?? species.PixelSize:F4}");
        status?.Invoke($"Box: {options.BoxSize ?? species.Size}");
        status?.Invoke($"Diameter: {options.Diameter ?? species.DiameterAngstrom} A");
        status?.Invoke($"Pre-rotate: {options.PrerotateParticles.ToString().ToLowerInvariant()}");

        List<int> selectedDevices = ValidateDevices(options.Devices);
        status?.Invoke("GPU devices: " + string.Join(",", selectedDevices));

        if (options.DryRun)
        {
            foreach (SourcePlan plan in plans)
                status?.Invoke($"Would export {plan.Series.RootName}: {plan.Particles.Length} particles");
            status?.Invoke("Validation OK.");
            return result;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputStarPath)!);
        RunTasks(plans, options, status);

        Star outputTable = BuildOutputStar(plans, outputStarPath, options.PrerotateParticles);
        outputTable.Save(outputStarPath);
        WriteMapping(mappingPath, populationPath, species, plans, outputStarPath);
        return result;
    }

    private static void ValidateOptions(Options options)
    {
        if (options == null)
            throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.PopulationPath) || !File.Exists(options.PopulationPath))
            throw new FileNotFoundException("Population file not found.", options.PopulationPath);
        if (string.IsNullOrWhiteSpace(options.SpeciesSelector))
            throw new ArgumentException("A species name, GUID, or path is required.");
        if (string.IsNullOrWhiteSpace(options.OutputStarPath))
            throw new ArgumentException("An output STAR path is required.");
        if (options.OutputPixelSize is <= 0 || options.BoxSize is <= 0 || options.Diameter is <= 0)
            throw new ArgumentException("--angpix, --box, and --diameter must be positive when specified.");
        if (options.BoxSize.HasValue && options.BoxSize.Value % 2 != 0)
            throw new ArgumentException("--box must be even.");
        if (options.NTilts is <= 0)
            throw new ArgumentException("--ntilts must be positive when specified.");
        if (options.ProcessesPerDevice < 1)
            throw new ArgumentException("--perdevice must be at least 1.");
    }

    private static Species ResolveSpecies(Population population, string selector)
    {
        Species[] species = population.Species.SelectMany(s => s.AllDescendants).ToArray();
        string fullSelectorPath = Path.GetFullPath(selector);
        Species[] matches = species.Where(s =>
            s.Name == selector ||
            s.GUID.ToString().Equals(selector, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(s.Path) == fullSelectorPath).ToArray();

        if (matches.Length == 1)
            return matches[0];
        if (matches.Length > 1)
            throw new InvalidOperationException($"More than one species matched '{selector}':\n" +
                                                string.Join("\n", matches.Select(s => $"  {s.Name} ({s.GUID}) {s.Path}")));

        throw new InvalidOperationException($"No species matched '{selector}'. Available species:\n" +
                                            string.Join("\n", species.Select(s => $"  {s.Name} ({s.GUID}) {s.Path}")));
    }

    private static List<SourcePlan> BuildPlans(Population population, Species species, Particle[] allParticles,
                                                Options options, string outputStarPath)
    {
        Dictionary<string, (DataSource Source, string Path)> sources = new();
        foreach (DataSource source in population.Sources)
        {
            if (!source.IsTiltSeries)
                continue;
            foreach (var file in source.Files)
            {
                string seriesPath = Path.GetFullPath(Path.Combine(source.FolderPath, file.Value));
                if (sources.ContainsKey(file.Key))
                    throw new InvalidOperationException($"Source hash '{file.Key}' is ambiguous between data sources.");
                sources.Add(file.Key, (source, seriesPath));
            }
        }

        Dictionary<string, List<int>> groups = new();
        for (int i = 0; i < allParticles.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(allParticles[i].SourceHash))
                throw new InvalidOperationException($"Species particle {i} has no SourceHash.");
            if (!groups.TryGetValue(allParticles[i].SourceHash, out List<int> group))
                groups.Add(allParticles[i].SourceHash, group = new List<int>());
            group.Add(i);
        }

        List<SourcePlan> result = new();
        foreach (var group in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (!sources.TryGetValue(group.Key, out var sourceFile))
                throw new InvalidOperationException($"No tilt-series source exists for SourceHash '{group.Key}'.");
            if (!File.Exists(sourceFile.Path))
                throw new FileNotFoundException($"Tilt-series metadata file does not exist for SourceHash '{group.Key}'.", sourceFile.Path);

            TiltSeries series = new TiltSeries(sourceFile.Path);
            if (series.GetDataHash() != group.Key)
                throw new InvalidOperationException($"Tilt series '{series.Name}' no longer matches SourceHash '{group.Key}'.");
            if (series.NTilts < 1)
                throw new InvalidOperationException($"Tilt series '{series.Name}' contains no tilts.");
            if (series.Dose == null || series.Dose.Length != series.NTilts)
                throw new InvalidOperationException($"Tilt series '{series.Name}' has invalid dose metadata.");
            string[] missingTiltMovies = series.TiltMoviePaths
                .Select(path => Path.Combine(series.DataOrProcessingDirectoryName, path))
                .Where(path => !File.Exists(path))
                .ToArray();
            if (missingTiltMovies.Length > 0)
                throw new FileNotFoundException($"Tilt series '{series.Name}' references missing tilt movie(s):\n" +
                                                string.Join("\n", missingTiltMovies));

            int[] particleIndices = group.Value.ToArray();
            Particle[] particles = particleIndices.Select(i => allParticles[i]).ToArray();
            decimal outputPixelSize = options.OutputPixelSize ?? species.PixelSize;
            int nTilts = options.NTilts ?? series.NTilts;
            if (sourceFile.Source.FrameLimit > 0)
                nTilts = Math.Min(nTilts, sourceFile.Source.FrameLimit);
            nTilts = Math.Min(nTilts, series.NTilts);

            ProcessingOptionsTomoSubReconstruction exportOptions = new()
            {
                PixelSize = sourceFile.Source.PixelSize,
                BinTimes = (decimal)Math.Log((double)(outputPixelSize / sourceFile.Source.PixelSizeMean), 2.0),
                GainPath = sourceFile.Source.GainPath,
                DefectsPath = sourceFile.Source.DefectsPath,
                GainFlipX = sourceFile.Source.GainFlipX,
                GainFlipY = sourceFile.Source.GainFlipY,
                GainTranspose = sourceFile.Source.GainTranspose,
                Dimensions = new float3((float)sourceFile.Source.DimensionsX,
                                        (float)sourceFile.Source.DimensionsY,
                                        (float)sourceFile.Source.DimensionsZ),
                Suffix = "_" + species.NameSafe,
                BoxSize = options.BoxSize ?? species.Size,
                ParticleDiameter = options.Diameter ?? species.DiameterAngstrom,
                Invert = options.Invert,
                NormalizeInput = options.NormalizeInput,
                NormalizeOutput = options.NormalizeOutput,
                PrerotateParticles = options.PrerotateParticles,
                DoLimitDose = options.LimitDose,
                NTilts = nTilts,
                MakeSparse = options.MakeSparse
            };

            (float3[] positions, float3[] angles) = BuildTrajectoryArrays(
                particles, GetInterpolationSteps(series.Dose), options.PrerotateParticles, options.AdditionalShiftAngstrom);
            string[] subtomoPaths = Enumerable.Range(0, particles.Length).Select(p => Path.Combine(series.SubtomoDir,
                $"{series.RootName}{exportOptions.Suffix}_{p:D7}_{exportOptions.BinnedPixelSizeMean:F2}A.mrc")).ToArray();
            string[] ctfPaths = Enumerable.Range(0, particles.Length).Select(p => Path.Combine(series.SubtomoDir,
                $"{series.RootName}{exportOptions.Suffix}_{p:D7}_ctf_{exportOptions.BinnedPixelSizeMean:F2}A.mrc")).ToArray();

            result.Add(new SourcePlan
            {
                Hash = group.Key,
                Source = sourceFile.Source,
                Series = series,
                Particles = particles,
                SpeciesParticleIndices = particleIndices,
                ExportOptions = exportOptions,
                Positions = positions,
                Angles = angles,
                SubtomoPaths = subtomoPaths,
                CtfPaths = ctfPaths
            });
        }
        return result;
    }

    private static void RunTasks(List<SourcePlan> plans, Options options, Action<string> status)
    {
        List<int> devices = ValidateDevices(options.Devices);
        string outputStarPath = Path.GetFullPath(options.OutputStarPath);
        string queueDirectory = string.IsNullOrWhiteSpace(options.TaskDirectory)
            ? Path.Combine(Path.GetDirectoryName(outputStarPath)!, "tasks")
            : Path.GetFullPath(options.TaskDirectory);
        string logDirectory = Path.Combine(Path.GetDirectoryName(outputStarPath)!, "logs");
        QueueLayout layout = new(queueDirectory);
        layout.EnsureDirectories();
        TaskQueue queue = new(layout);
        queue.Clear();
        WorkPool pool = new(layout, queue);
        List<TaskItem> tasks = plans.Select((plan, i) =>
        {
            TaskItem task = new()
            {
                TaskId = $"{i + 1:D7}-export-{plan.Series.RootName}",
                Stage = "export_subtomos",
                RequiresGpu = true,
                Main = new[] { WorkerCommands.TomoExportParticleSubtomos(plan.Series.Path, plan.ExportOptions, plan.Positions, plan.Angles) }
            };
            task.ComputeInitFingerprint();
            return task;
        }).ToList();

        Directory.CreateDirectory(logDirectory);
        LocalProvisioner provisioner = new(layout.Root, devices.ToArray(), options.ProcessesPerDevice, logDir: logDirectory);
        Scheduler scheduler = new(layout, queue, provisioner, Math.Min(tasks.Count, devices.Count * options.ProcessesPerDevice), logDir: logDirectory);
        pool.Enqueue(tasks);
        using CancellationTokenSource cancellation = new();
        Thread schedulerThread = new(() => scheduler.RunToDrain(cancel: cancellation.Token)) { IsBackground = true };
        schedulerThread.Start();

        int completed = 0;
        try
        {
            Dictionary<string, WorkResult> results = pool.Distribute(tasks, result =>
            {
                int index = tasks.FindIndex(t => t.TaskId == result.TaskId);
                SourcePlan plan = plans[index];
                int done = Interlocked.Increment(ref completed);
                if (result.Outcome != WorkOutcome.Done)
                    throw new InvalidOperationException($"Export failed for '{plan.Series.Name}': {result.Error}");
                status?.Invoke($"[{done}/{plans.Count}] {plan.Series.RootName}: {plan.Particles.Length} particles");
            }, pollMs: 500);

            WorkResult failed = results.Values.FirstOrDefault(r => r.Outcome != WorkOutcome.Done);
            if (failed != null)
                throw new InvalidOperationException($"Export task '{failed.TaskId}' failed: {failed.Error}");
        }
        finally
        {
            cancellation.Cancel();
            schedulerThread.Join();
            provisioner.Shutdown();
            scheduler.Dispose();
        }
    }

    private static List<int> ValidateDevices(IReadOnlyList<int> requestedDevices)
    {
        int gpuCount = GPU.GetDeviceCount();
        List<int> devices = requestedDevices == null || requestedDevices.Count == 0
            ? Helper.ArrayOfSequence(0, gpuCount, 1).ToList()
            : requestedDevices.Distinct().ToList();
        if (devices.Count == 0 || devices.Any(d => d < 0 || d >= gpuCount))
            throw new ArgumentException($"--device_list must contain unique GPU IDs between 0 and {gpuCount - 1}.");
        return devices;
    }

    private static Star BuildOutputStar(IEnumerable<SourcePlan> plans, string outputStarPath, bool prerotate)
    {
        Star table = new(new[]
        {
            "rlnMagnification", "rlnDetectorPixelSize", "rlnCoordinateX", "rlnCoordinateY", "rlnCoordinateZ",
            "rlnAngleRot", "rlnAngleTilt", "rlnAnglePsi", "rlnImageName", "rlnCtfImage", "rlnRandomSubset",
            "rlnPixelSize", "rlnVoltage", "rlnSphericalAberration", "rlnMicrographName"
        });
        Uri starUri = new(outputStarPath);
        foreach (SourcePlan plan in plans)
        for (int p = 0; p < plan.Particles.Length; p++)
        {
            float3 position = plan.Positions[p * plan.Series.NTilts];
            float3 originalAngles = plan.Particles[p].Angles[0];
            table.AddRow(new[]
            {
                "10000.0",
                plan.ExportOptions.BinnedPixelSizeMean.ToString("F5", CultureInfo.InvariantCulture),
                (position.X / (float)plan.ExportOptions.BinnedPixelSizeMean).ToString("F5", CultureInfo.InvariantCulture),
                (position.Y / (float)plan.ExportOptions.BinnedPixelSizeMean).ToString("F5", CultureInfo.InvariantCulture),
                (position.Z / (float)plan.ExportOptions.BinnedPixelSizeMean).ToString("F5", CultureInfo.InvariantCulture),
                (prerotate ? 0 : originalAngles.X).ToString("F5", CultureInfo.InvariantCulture),
                (prerotate ? 0 : originalAngles.Y).ToString("F5", CultureInfo.InvariantCulture),
                (prerotate ? 0 : originalAngles.Z).ToString("F5", CultureInfo.InvariantCulture),
                starUri.MakeRelativeUri(new Uri(plan.SubtomoPaths[p])).ToString(),
                starUri.MakeRelativeUri(new Uri(plan.CtfPaths[p])).ToString(),
                (plan.Particles[p].RandomSubset + 1).ToString(CultureInfo.InvariantCulture),
                plan.ExportOptions.BinnedPixelSizeMean.ToString("F5", CultureInfo.InvariantCulture),
                plan.Series.CTF.Voltage.ToString("F1", CultureInfo.InvariantCulture),
                plan.Series.CTF.Cs.ToString("F3", CultureInfo.InvariantCulture),
                plan.Series.Name
            });
        }
        return table;
    }

    private static void WriteMapping(string mappingPath, string populationPath, Species species,
                                     IEnumerable<SourcePlan> plans, string outputStarPath)
    {
        Uri starUri = new(outputStarPath);
        List<MappingParticle> particles = new();
        int exportIndex = 0;
        foreach (SourcePlan plan in plans)
        for (int p = 0; p < plan.Particles.Length; p++)
        {
            particles.Add(new MappingParticle
            {
                export_index = exportIndex++,
                image_name = starUri.MakeRelativeUri(new Uri(plan.SubtomoPaths[p])).ToString(),
                ctf_image = starUri.MakeRelativeUri(new Uri(plan.CtfPaths[p])).ToString(),
                source_hash = plan.Hash,
                source_name = plan.Particles[p].SourceName,
                species_particle_index = plan.SpeciesParticleIndices[p]
            });
        }
        MappingFile mapping = new()
        {
            population = populationPath,
            species = species.GUID.ToString(),
            species_name = species.Name,
            species_path = species.Path,
            species_version = species.Version,
            warp_version = typeof(SpeciesSubtomoExporter).Assembly.GetName().Version?.ToString(),
            particles = particles.ToArray()
        };
        File.WriteAllText(mappingPath, JsonSerializer.Serialize(mapping, new JsonSerializerOptions { WriteIndented = true }));
    }
}
