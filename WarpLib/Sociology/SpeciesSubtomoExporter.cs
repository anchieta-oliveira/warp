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
        public string SubtomoDirectory { get; init; }
        public bool Output2D { get; init; }
        public string ParticleSeriesDirectory { get; init; }
        public bool DontPremultiply { get; init; }
        public int MaxMissingTilts { get; init; } = 5;
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
        public string SubtomoDirectory { get; init; }
        public string ParticleSeriesDirectory { get; init; }
        public string ParticleTablePath { get; init; }
        public string[] ParticleSeriesPaths { get; init; }
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
        if (options.Output2D)
        {
            outputFiles.Add(Path.Combine(Path.GetDirectoryName(outputStarPath)!,
                Path.GetFileNameWithoutExtension(outputStarPath) + "_tomograms.star"));
            outputFiles.Add(Path.Combine(Path.GetDirectoryName(outputStarPath)!,
                Path.GetFileNameWithoutExtension(outputStarPath) + "_optimisation_set.star"));
            outputFiles.Add(Path.Combine(Path.GetDirectoryName(outputStarPath)!, "dummy_tiltseries.mrc"));
            outputFiles.AddRange(System.Linq.Enumerable.SelectMany(plans, p => p.ParticleSeriesPaths));
            outputFiles.AddRange(System.Linq.Enumerable.Select(plans, p => Path.Combine(p.ParticleSeriesDirectory,
                $"{p.Series.RootName}_{p.ExportOptions.BinnedPixelSizeMean:F2}A_average.mrcs")));
        }
        else
        {
            outputFiles.AddRange(System.Linq.Enumerable.SelectMany(plans, p => p.SubtomoPaths));
            outputFiles.AddRange(System.Linq.Enumerable.SelectMany(plans, p => p.CtfPaths));
            outputFiles.AddRange(System.Linq.Enumerable.Select(plans, p => Path.Combine(p.SubtomoDirectory,
                $"{p.Series.RootName}{p.ExportOptions.Suffix}_{p.ExportOptions.BinnedPixelSizeMean:F2}A_average.mrc")));
        }
        if (!options.Overwrite)
        {
            string[] existing = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Distinct(
                System.Linq.Enumerable.Where(outputFiles, File.Exists)));
            if (existing.Length > 0)
                throw new IOException("Refusing to overwrite existing export files. Use --overwrite to replace them:\n" +
                                      string.Join("\n", existing));
        }

        Result result = new()
        {
            PopulationPath = populationPath,
            SpeciesName = species.Name,
            SpeciesGuid = species.GUID,
            ParticleCount = plans.Sum(plan => plan.Particles.Length),
            SourceCount = plans.Count,
            OutputStarPath = outputStarPath,
            MappingPath = mappingPath
        };

        status?.Invoke($"Species: {species.Name}");
        status?.Invoke($"Particles: {result.ParticleCount}");
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

        if (options.Output2D)
            Build2DOutputStars(plans, outputStarPath, options);
        else
            BuildOutputStar(plans, outputStarPath, options.PrerotateParticles).Save(outputStarPath);
        WriteMapping(mappingPath, populationPath, species, plans, outputStarPath, options.Output2D);
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
        if (options.MaxMissingTilts < 0)
            throw new ArgumentException("--max_missing_tilts must not be negative.");
        if (options.ProcessesPerDevice < 1)
            throw new ArgumentException("--perdevice must be at least 1.");
        if (options.Output2D && !string.IsNullOrWhiteSpace(options.SubtomoDirectory))
            throw new ArgumentException("--subtomo_dir is only valid for 3D export.");
        if (options.Output2D && (options.PrerotateParticles || options.MakeSparse || !options.NormalizeOutput))
            throw new ArgumentException("--prerotate, --make_sparse, and --dont_normalize_output are only valid for 3D export.");
        if (!options.Output2D && (!string.IsNullOrWhiteSpace(options.ParticleSeriesDirectory) || options.DontPremultiply || options.MaxMissingTilts != 5))
            throw new ArgumentException("--particle_series_dir, --dont_premultiply, and --max_missing_tilts are only valid for 2D export.");
    }

    private static Species ResolveSpecies(Population population, string selector)
    {
        Species[] species = System.Linq.Enumerable.ToArray(
            System.Linq.Enumerable.SelectMany(population.Species, s => s.AllDescendants));
        string fullSelectorPath = Path.GetFullPath(selector);
        Species[] matches = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(species, s =>
            s.Name == selector ||
            s.GUID.ToString().Equals(selector, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(s.Path) == fullSelectorPath));

        if (matches.Length == 1)
            return matches[0];
        if (matches.Length > 1)
            throw new InvalidOperationException($"More than one species matched '{selector}':\n" +
                                                string.Join("\n", System.Linq.Enumerable.Select(matches, s => $"  {s.Name} ({s.GUID}) {s.Path}")));

        throw new InvalidOperationException($"No species matched '{selector}'. Available species:\n" +
                                            string.Join("\n", System.Linq.Enumerable.Select(species, s => $"  {s.Name} ({s.GUID}) {s.Path}")));
    }

    private static List<SourcePlan> BuildPlans(Population population, Species species, Particle[] allParticles,
                                                Options options, string outputStarPath)
    {
        string subtomoRoot = string.IsNullOrWhiteSpace(options.SubtomoDirectory)
            ? null
            : Path.GetFullPath(options.SubtomoDirectory);
        string particleSeriesRoot = string.IsNullOrWhiteSpace(options.ParticleSeriesDirectory)
            ? null
            : Path.GetFullPath(options.ParticleSeriesDirectory);
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
        foreach (var group in System.Linq.Enumerable.OrderBy(groups, g => g.Key, StringComparer.Ordinal))
        {
            if (!sources.TryGetValue(group.Key, out var sourceFile))
                throw new InvalidOperationException($"No tilt-series source exists for SourceHash '{group.Key}'.");

            TiltSeries series = new TiltSeries(sourceFile.Path);
            if (series.GetDataHash() != group.Key)
                throw new InvalidOperationException($"Tilt series '{series.Name}' no longer matches SourceHash '{group.Key}'.");
            if (series.NTilts < 1)
                throw new InvalidOperationException($"Tilt series '{series.Name}' contains no tilts.");
            if (series.Dose == null || series.Dose.Length != series.NTilts)
                throw new InvalidOperationException($"Tilt series '{series.Name}' has invalid dose metadata.");
            string[] missingTiltMovies = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(
                System.Linq.Enumerable.Select(series.TiltMoviePaths,
                    path => new Movie(Path.Combine(series.DataOrProcessingDirectoryName, path)).DataPath),
                path => !File.Exists(path)));
            if (missingTiltMovies.Length > 0)
                throw new FileNotFoundException($"Tilt series '{series.Name}' references missing tilt movie(s):\n" +
                                                string.Join("\n", missingTiltMovies));

            int[] particleIndices = group.Value.ToArray();
            Particle[] particles = System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Select(particleIndices, i => allParticles[i]));
            decimal outputPixelSize = options.OutputPixelSize ?? species.PixelSize;
            int nTilts = options.NTilts ?? series.NTilts;
            if (sourceFile.Source.FrameLimit > 0)
                nTilts = Math.Min(nTilts, sourceFile.Source.FrameLimit);
            nTilts = Math.Min(nTilts, series.NTilts);
            string subtomoDirectory = subtomoRoot == null
                ? series.SubtomoDir
                : Path.Combine(subtomoRoot, series.RootName);
            string particleSeriesDirectory = particleSeriesRoot == null
                ? Path.Combine(Path.GetDirectoryName(outputStarPath)!, "particle_series", series.RootName)
                : Path.Combine(particleSeriesRoot, series.RootName);

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
                MakeSparse = options.MakeSparse,
                SubtomoOutputDirectory = subtomoDirectory,
                ParticleSeriesOutputDirectory = particleSeriesDirectory,
                DontPremultiply = options.DontPremultiply
            };

            (float3[] positions, float3[] angles) = BuildTrajectoryArrays(
                particles, GetInterpolationSteps(series.Dose), options.PrerotateParticles, options.AdditionalShiftAngstrom);
            if (options.Output2D)
            {
                series.VolumeDimensionsPhysical = exportOptions.DimensionsPhysical;
                List<int> retained = Enumerable.Range(0, particles.Length).Where(p =>
                    !RelionParticleSeriesExport.ShouldExcludeParticle(GetVisibility(series, exportOptions,
                        positions.Skip(p * series.NTilts).Take(series.NTilts).ToArray()), options.MaxMissingTilts)).ToList();
                if (retained.Count == 0)
                    continue;
                particles = retained.Select(p => particles[p]).ToArray();
                particleIndices = retained.Select(p => particleIndices[p]).ToArray();
                (positions, angles) = BuildTrajectoryArrays(particles, GetInterpolationSteps(series.Dose),
                    options.PrerotateParticles, options.AdditionalShiftAngstrom);
            }
            string[] subtomoPaths = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Range(0, particles.Length), p => Path.Combine(subtomoDirectory,
                    $"{series.RootName}{exportOptions.Suffix}_{p:D7}_{exportOptions.BinnedPixelSizeMean:F2}A.mrc")));
            string[] ctfPaths = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Range(0, particles.Length), p => Path.Combine(subtomoDirectory,
                    $"{series.RootName}{exportOptions.Suffix}_{p:D7}_ctf_{exportOptions.BinnedPixelSizeMean:F2}A.mrc")));

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
                CtfPaths = ctfPaths,
                SubtomoDirectory = subtomoDirectory,
                ParticleSeriesDirectory = particleSeriesDirectory,
                ParticleTablePath = Path.Combine(particleSeriesDirectory, series.RootName + "_temp.star"),
                ParticleSeriesPaths = Enumerable.Range(0, particles.Length).Select(p => Path.Combine(particleSeriesDirectory,
                    $"{series.RootName}_{exportOptions.BinnedPixelSizeMean:F2}A_{p + 1:D6}.mrcs")).ToArray()
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
        List<TaskItem> tasks = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(plans, (plan, i) =>
        {
            TaskItem task = new()
            {
                TaskId = $"{i + 1:D7}-export-{plan.Series.RootName}",
                Stage = "export_subtomos",
                RequiresGpu = true,
                Main = new[]
                {
                    options.Output2D
                        ? WorkerCommands.TomoExportParticleSeries(plan.Series.Path, plan.ExportOptions,
                            plan.Positions, plan.Angles, outputStarPath, plan.ParticleTablePath)
                        : WorkerCommands.TomoExportParticleSubtomos(plan.Series.Path, plan.ExportOptions,
                            plan.Positions, plan.Angles)
                }
            };
            task.ComputeInitFingerprint();
            return task;
        }));

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

            WorkResult failed = System.Linq.Enumerable.FirstOrDefault(results.Values,
                r => r.Outcome != WorkOutcome.Done);
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
            ? new List<int>(Helper.ArrayOfSequence(0, gpuCount, 1))
            : System.Linq.Enumerable.ToList(System.Linq.Enumerable.Distinct(requestedDevices));
        if (devices.Count == 0 || System.Linq.Enumerable.Any(devices, d => d < 0 || d >= gpuCount))
            throw new ArgumentException($"--device_list must contain unique GPU IDs between 0 and {gpuCount - 1}.");
        return devices;
    }

    private static string GetVisibility(TiltSeries series, ProcessingOptionsTomoSubReconstruction options,
                                        float3[] positions)
    {
        bool[] visibility = series.GetPositionInAllTilts(positions).Select(p =>
            p.X > options.ParticleDiameter / 2 && p.X < series.ImageDimensionsPhysical.X - options.ParticleDiameter / 2 &&
            p.Y > options.ParticleDiameter / 2 && p.Y < series.ImageDimensionsPhysical.Y - options.ParticleDiameter / 2).ToArray();
        for (int tilt = 0; tilt < visibility.Length; tilt++)
            visibility[tilt] &= series.UseTilt[tilt];
        return "[" + string.Join(',', GetUsedTiltIndices(series, options).Select(tilt => visibility[tilt] ? "1" : "0")) + "]";
    }

    private static IEnumerable<int> GetUsedTiltIndices(TiltSeries series, ProcessingOptionsTomoSubReconstruction options) =>
        options.DoLimitDose ? series.IndicesSortedDose.Take(options.NTilts).OrderBy(i => i) : series.IndicesSortedDose.OrderBy(i => i);

    private static void Build2DOutputStars(IEnumerable<SourcePlan> plans, string outputStarPath, Options options)
    {
        Dictionary<string, Star> tables = new();
        int opticsGroup = 0;
        foreach (SourcePlan plan in plans)
        {
            int group = ++opticsGroup;
            Star particles = new(plan.ParticleTablePath);
            particles.ModifyAllValuesInColumn("rlnOpticsGroup", _ => group.ToString(CultureInfo.InvariantCulture));
            float pixelSize = (float)plan.ExportOptions.BinnedPixelSizeMean;
            particles.ModifyAllValuesInColumn("rlnCoordinateX", (_, i) =>
                (plan.Particles[i].CoordinatesMean.X / pixelSize).ToString("F3", CultureInfo.InvariantCulture));
            particles.ModifyAllValuesInColumn("rlnCoordinateY", (_, i) =>
                (plan.Particles[i].CoordinatesMean.Y / pixelSize).ToString("F3", CultureInfo.InvariantCulture));
            particles.ModifyAllValuesInColumn("rlnCoordinateZ", (_, i) =>
                (plan.Particles[i].CoordinatesMean.Z / pixelSize).ToString("F3", CultureInfo.InvariantCulture));
            particles.ModifyAllValuesInColumn("rlnAngleRot", (_, i) =>
                plan.Particles[i].AnglesMean.X.ToString("F3", CultureInfo.InvariantCulture));
            particles.ModifyAllValuesInColumn("rlnAngleTilt", (_, i) =>
                plan.Particles[i].AnglesMean.Y.ToString("F3", CultureInfo.InvariantCulture));
            particles.ModifyAllValuesInColumn("rlnAnglePsi", (_, i) =>
                plan.Particles[i].AnglesMean.Z.ToString("F3", CultureInfo.InvariantCulture));
            string[] randomSubsets = plan.Particles.Select(p =>
                (p.RandomSubset + 1).ToString(CultureInfo.InvariantCulture)).ToArray();
            if (particles.HasColumn("rlnRandomSubset"))
                particles.ModifyAllValuesInColumn("rlnRandomSubset", (_, i) => randomSubsets[i]);
            else
                particles.AddColumn("rlnRandomSubset", randomSubsets);
            foreach (string column in plan.Particles.SelectMany(p => p.Extra?.Keys ?? Enumerable.Empty<string>()).Distinct())
                if (!particles.HasColumn(column))
                    particles.AddColumn(column, plan.Particles.Select(p =>
                        p.Extra != null && p.Extra.TryGetValue(column, out string value) ? value : "?").ToArray());
            if (particles.HasColumn("rlnCtfDataAreCtfPremultiplied"))
                particles.ModifyAllValuesInColumn("rlnCtfDataAreCtfPremultiplied", _ => options.DontPremultiply ? "0" : "1");

            tables.Add(plan.Series.RootName + "_particles", particles);
            tables.Add(plan.Series.RootName + "_optics", Build2DOptics(plan, group, !options.DontPremultiply));
            tables.Add(plan.Series.RootName + "_tomograms_global", Build2DTomogramsGlobal(plan, group));
            tables.Add(plan.Series.RootName + "_tomograms_tiltseries", Build2DTomogramsTilts(plan));
        }
        RelionParticleSeriesExport.WriteOutputFiles(tables, outputStarPath, options.MaxMissingTilts, outputStarPath);
        foreach (SourcePlan plan in plans)
            File.Delete(plan.ParticleTablePath);
    }

    private static Star Build2DOptics(SourcePlan plan, int opticsGroup, bool premultiplied)
    {
        float pixelSize = (float)plan.ExportOptions.BinnedPixelSizeMean;
        return new Star(new[]
        {
            new[] { opticsGroup.ToString(CultureInfo.InvariantCulture) }, new[] { $"opticsGroup{opticsGroup}" },
            new[] { plan.Series.CTF.Cs.ToString("F3", CultureInfo.InvariantCulture) }, new[] { plan.Series.CTF.Voltage.ToString("F3", CultureInfo.InvariantCulture) },
            new[] { pixelSize.ToString("F5", CultureInfo.InvariantCulture) }, new[] { premultiplied ? "1" : "0" }, new[] { "2" },
            new[] { "1.00000" }, new[] { pixelSize.ToString("F5", CultureInfo.InvariantCulture) },
            new[] { plan.ExportOptions.BoxSize.ToString(CultureInfo.InvariantCulture) }, new[] { plan.Series.CTF.Amplitude.ToString("F3", CultureInfo.InvariantCulture) }
        }, new[]
        {
            "rlnOpticsGroup", "rlnOpticsGroupName", "rlnSphericalAberration", "rlnVoltage",
            "rlnTomoTiltSeriesPixelSize", "rlnCtfDataAreCtfPremultiplied", "rlnImageDimensionality",
            "rlnTomoSubtomogramBinning", "rlnImagePixelSize", "rlnImageSize", "rlnAmplitudeContrast"
        });
    }

    private static Star Build2DTomogramsGlobal(SourcePlan plan, int opticsGroup)
    {
        List<int> doseOrderedTilts = plan.ExportOptions.DoLimitDose
            ? plan.Series.IndicesSortedDose.Take(plan.ExportOptions.NTilts).ToList()
            : plan.Series.IndicesSortedDose.ToList();
        float dose = doseOrderedTilts.Count > 1
            ? plan.Series.Dose[doseOrderedTilts[1]] - plan.Series.Dose[doseOrderedTilts[0]]
            : plan.Series.Dose[doseOrderedTilts[0]];
        int3 dimensions = RelionParticleSeriesExport.GetVirtualTomogramDimensions(plan.ExportOptions.DimensionsPhysical,
            (float)plan.ExportOptions.BinnedPixelSizeMean);
        return new Star(new[] { new[] { plan.Series.RootName + ".tomostar" }, new[] { "dummy.mrc" }, new[] { doseOrderedTilts.Count.ToString(CultureInfo.InvariantCulture) }, new[] { dimensions.X.ToString(CultureInfo.InvariantCulture) }, new[] { dimensions.Y.ToString(CultureInfo.InvariantCulture) }, new[] { dimensions.Z.ToString(CultureInfo.InvariantCulture) }, new[] { RelionParticleSeriesExport.GetRelionHand(plan.Series.AreAnglesInverted).ToString("F1", CultureInfo.InvariantCulture) }, new[] { $"opticsGroup{opticsGroup}" }, new[] { ((float)plan.ExportOptions.BinnedPixelSizeMean).ToString("F5", CultureInfo.InvariantCulture) }, new[] { plan.Series.CTF.Voltage.ToString("F3", CultureInfo.InvariantCulture) }, new[] { plan.Series.CTF.Cs.ToString("F3", CultureInfo.InvariantCulture) }, new[] { plan.Series.CTF.Amplitude.ToString("F3", CultureInfo.InvariantCulture) }, new[] { dose.ToString("F3", CultureInfo.InvariantCulture) } },
            "rlnTomoName", "rlnTomoTiltSeriesName", "rlnTomoFrameCount", "rlnTomoSizeX", "rlnTomoSizeY", "rlnTomoSizeZ", "rlnTomoHand", "rlnOpticsGroupName", "rlnTomoTiltSeriesPixelSize", "rlnVoltage", "rlnSphericalAberration", "rlnAmplitudeContrast", "rlnTomoImportFractionalDose");
    }

    private static Star Build2DTomogramsTilts(SourcePlan plan)
    {
        Star table = new(new[] { "rlnTomoProjX", "rlnTomoProjY", "rlnTomoProjZ", "rlnTomoProjW", "rlnDefocusU", "rlnDefocusV", "rlnDefocusAngle", "rlnPhaseShift", "rlnCtfScalefactor", "rlnMicrographPreExposure" });
        float3[] angles = plan.Series.GetAngleInAllTilts(plan.ExportOptions.DimensionsPhysical * 0.5f);
        foreach (int tilt in GetUsedTilts(plan))
        {
            Matrix3 matrix = Matrix3.Euler(angles[tilt]);
            float3 imageCoords = plan.Series.GetPositionsInOneTilt(new[] { plan.ExportOptions.DimensionsPhysical * 0.5f }, tilt).First();
            CTF ctf = plan.Series.GetCTFParamsForOneTilt((float)plan.ExportOptions.PixelSize, new[] { imageCoords.Z }, new[] { imageCoords }, tilt, true).First();
            table.AddRow(new[] { $"[{matrix.M11},{matrix.M12},{matrix.M13},0]", $"[{matrix.M21},{matrix.M22},{matrix.M23},0]", $"[{matrix.M31},{matrix.M32},{matrix.M33},0]", "[0,0,0,1]", ((ctf.Defocus + ctf.DefocusDelta / 2) * 1e4M).ToString("F1", CultureInfo.InvariantCulture), ((ctf.Defocus - ctf.DefocusDelta / 2) * 1e4M).ToString("F1", CultureInfo.InvariantCulture), ctf.DefocusAngle.ToString("F3", CultureInfo.InvariantCulture), RelionParticleSeriesExport.GetPhaseShiftDegrees(ctf.PhaseShift).ToString("F3", CultureInfo.InvariantCulture), ctf.Scale.ToString("F3", CultureInfo.InvariantCulture), plan.Series.Dose[tilt].ToString("F3", CultureInfo.InvariantCulture) });
        }
        return table;
    }

    private static List<int> GetUsedTilts(SourcePlan plan)
    {
        List<int> tilts = plan.ExportOptions.DoLimitDose
            ? plan.Series.IndicesSortedDose.Take(plan.ExportOptions.NTilts).ToList()
            : plan.Series.IndicesSortedDose.ToList();
        tilts.Sort();
        return tilts;
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
                                     IEnumerable<SourcePlan> plans, string outputStarPath, bool output2D)
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
                image_name = starUri.MakeRelativeUri(new Uri(output2D ? plan.ParticleSeriesPaths[p] : plan.SubtomoPaths[p])).ToString(),
                ctf_image = output2D ? null : starUri.MakeRelativeUri(new Uri(plan.CtfPaths[p])).ToString(),
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
