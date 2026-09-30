using CommandLine;
using System;
using System.Collections.Generic;
using System.Linq;
using Warp.Sociology;
using Warp.Tools;

namespace MTools.Commands
{
    [Verb("export_subtomos", HelpText = "Export M-refined particle trajectories as RELION subtomograms")]
    [CommandRunner(typeof(ExportSubtomos))]
    class ExportSubtomosOptions
    {
        [Option('p', "population", Required = true, HelpText = "Path to the .population file.")]
        public string Population { get; set; }

        [Option('s', "species", Required = true, HelpText = "Species name, GUID, or path to its .species file.")]
        public string Species { get; set; }

        [Option('o', "output", Required = true, HelpText = "Output RELION particle STAR file.")]
        public string Output { get; set; }

        [Option("device_list", HelpText = "Space-separated GPU IDs. Default: all GPUs.")]
        public IEnumerable<int> DeviceList { get; set; }

        [Option("perdevice", Default = 1, HelpText = "Number of WarpWorker2 processes per GPU.")]
        public int ProcessesPerDevice { get; set; }

        [Option("task_dir", HelpText = "Filesystem work queue directory. Default: tasks next to --output.")]
        public string TaskDirectory { get; set; }

        [Option("subtomo_dir", HelpText = "Root directory for native MRC outputs. One subdirectory per tilt series is created.")]
        public string SubtomoDirectory { get; set; }

        [Option("2d", HelpText = "Export RELION 2D particle series instead of 3D subtomograms.")]
        public bool Output2D { get; set; }

        [Option("particle_series_dir", HelpText = "Root directory for 2D particle-series MRC stacks. One subdirectory per tilt series is created.")]
        public string ParticleSeriesDirectory { get; set; }

        [Option("dont_premultiply", HelpText = "Do not premultiply 2D particle series by CTFs or RELION weights.")]
        public bool DontPremultiply { get; set; }

        [Option("max_missing_tilts", Default = 5, HelpText = "Exclude 2D particles missing more than this many selected tilts.")]
        public int MaxMissingTilts { get; set; }

        [Option("angpix", HelpText = "Output pixel size in Angstrom. Default: Species.PixelSize.")]
        public decimal? AngPix { get; set; }

        [Option("box", HelpText = "Output box size. Default: Species.Size.")]
        public int? Box { get; set; }

        [Option("diameter", HelpText = "Particle diameter in Angstrom. Default: Species.DiameterAngstrom.")]
        public int? Diameter { get; set; }

        [Option("prerotate", HelpText = "Pre-rotate exported subtomograms into their refined particle orientations.")]
        public bool Prerotate { get; set; }

        [Option("limit_dose", HelpText = "Use only the lowest-dose tilts selected by --ntilts.")]
        public bool LimitDose { get; set; }

        [Option("ntilts", HelpText = "Number of tilts to retain when --limit_dose is set. Default: all available tilts.")]
        public int? NTilts { get; set; }

        [Option("make_sparse", HelpText = "Make CTF volumes sparse.")]
        public bool MakeSparse { get; set; }

        [Option("shift_x", Default = 0f, HelpText = "Additional X shift in Angstrom.")]
        public float ShiftX { get; set; }

        [Option("shift_y", Default = 0f, HelpText = "Additional Y shift in Angstrom.")]
        public float ShiftY { get; set; }

        [Option("shift_z", Default = 0f, HelpText = "Additional Z shift in Angstrom.")]
        public float ShiftZ { get; set; }

        [Option("normalize_input", Default = true, HelpText = "Normalize input tilt images after high-pass filtering.")]
        public bool NormalizeInput { get; set; }

        [Option("normalize_output", Default = true, HelpText = "Normalize output subtomograms.")]
        public bool NormalizeOutput { get; set; }

        [Option("invert", Default = true, HelpText = "Invert input contrast.")]
        public bool Invert { get; set; }

        [Option("dont_normalize_input", HelpText = "Disable input normalization.")]
        public bool DontNormalizeInput { get; set; }

        [Option("dont_normalize_output", HelpText = "Disable output normalization.")]
        public bool DontNormalizeOutput { get; set; }

        [Option("dont_invert", HelpText = "Disable input contrast inversion.")]
        public bool DontInvert { get; set; }

        [Option("overwrite", HelpText = "Allow replacement of existing STAR, mapping, and native subtomogram outputs.")]
        public bool Overwrite { get; set; }

        [Option("dry_run", HelpText = "Validate inputs and show the planned export without creating files.")]
        public bool DryRun { get; set; }
    }

    class ExportSubtomos : BaseCommand
    {
        public override void Run(object options)
        {
            ExportSubtomosOptions cli = options as ExportSubtomosOptions;
            try
            {
                SpeciesSubtomoExporter exporter = new SpeciesSubtomoExporter();
                SpeciesSubtomoExporter.Result result = exporter.Run(new SpeciesSubtomoExporter.Options
                {
                    PopulationPath = cli.Population,
                    SpeciesSelector = cli.Species,
                    OutputStarPath = cli.Output,
                    Devices = cli.DeviceList?.ToArray(),
                    ProcessesPerDevice = cli.ProcessesPerDevice,
                    TaskDirectory = cli.TaskDirectory,
                    SubtomoDirectory = cli.SubtomoDirectory,
                    Output2D = cli.Output2D,
                    ParticleSeriesDirectory = cli.ParticleSeriesDirectory,
                    DontPremultiply = cli.DontPremultiply,
                    MaxMissingTilts = cli.MaxMissingTilts,
                    OutputPixelSize = cli.AngPix,
                    BoxSize = cli.Box,
                    Diameter = cli.Diameter,
                    PrerotateParticles = cli.Prerotate,
                    LimitDose = cli.LimitDose,
                    NTilts = cli.NTilts,
                    MakeSparse = cli.MakeSparse,
                    AdditionalShiftAngstrom = new float3(cli.ShiftX, cli.ShiftY, cli.ShiftZ),
                    NormalizeInput = cli.DontNormalizeInput ? false : cli.NormalizeInput,
                    NormalizeOutput = cli.DontNormalizeOutput ? false : cli.NormalizeOutput,
                    Invert = cli.DontInvert ? false : cli.Invert,
                    Overwrite = cli.Overwrite,
                    DryRun = cli.DryRun
                }, Console.WriteLine);

                if (!cli.DryRun)
                {
                    Console.WriteLine($"Exported particles: {result.ParticleCount}");
                    Console.WriteLine($"STAR: {result.OutputStarPath}");
                    Console.WriteLine($"Mapping: {result.MappingPath}");
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("ERROR: " + exception);
            }
        }
    }
}
