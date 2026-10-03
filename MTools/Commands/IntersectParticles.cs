using CommandLine;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Warp;
using Warp.Sociology;
using Warp.Tools;

namespace MTools.Commands
{
    [Verb("intersect_particles", HelpText = "Select species particles by their intersection with an M or RELION particle table")]
    [CommandRunner(typeof(IntersectParticles))]
    class IntersectParticlesOptions
    {
        [Option('p', "population", Required = true, HelpText = "Path to the .population file.")]
        public string Population { get; set; }

        [Option('s', "species", Required = true, HelpText = "Species name, GUID, or path to its .species file.")]
        public string Species { get; set; }

        [Option("particles_relion", HelpText = "Path to a RELION particle STAR file.")]
        public string ParticlesRelion { get; set; }

        [Option("particles_m", HelpText = "Path to an M particle STAR file.")]
        public string ParticlesM { get; set; }

        [Option("mapping", HelpText = "Mapping JSON emitted by export_subtomos, used to resolve RELION 2D tomogram names.")]
        public string Mapping { get; set; }

        [Option("tolerance", Default = 10f, HelpText = "Maximum coordinate distance in Angstrom for particles to be in both sets.")]
        public float Tolerance { get; set; }

        [Option("keep_only_old", HelpText = "Keep existing species particles not within tolerance of an imported particle.")]
        public bool KeepOnlyOld { get; set; }

        [Option("keep_in_both", HelpText = "Keep existing species particles within tolerance of an imported particle.")]
        public bool KeepInBoth { get; set; }

        [Option("keep_only_new", HelpText = "Keep imported particles not within tolerance of an existing species particle.")]
        public bool KeepOnlyNew { get; set; }

        [Option("angpix_coords", HelpText = "Override RELION coordinate pixel size in Angstrom.")]
        public float? AngPixRelionPos { get; set; }

        [Option("angpix_shifts", HelpText = "Override RELION origin-shift pixel size in Angstrom.")]
        public float? AngPixRelionShifts { get; set; }

        [Option("ignore_unmatched", HelpText = "Continue when imported particles cannot be resolved to a population source.")]
        public bool IgnoreUnmatched { get; set; }

        [Option("apply", HelpText = "Replace species particles with the selected set. Without this flag, only report results.")]
        public bool Apply { get; set; }
    }

    class IntersectParticles : BaseCommand
    {
        private sealed class MappingFile { public MappingParticle[] particles { get; set; } }
        private sealed class MappingParticle { public string source_hash { get; set; } public string output_name { get; set; } }

        public override void Run(object options)
        {
            base.Run(options);
            IntersectParticlesOptions cli = options as IntersectParticlesOptions;
            try
            {
                if (string.IsNullOrEmpty(cli.ParticlesRelion) == string.IsNullOrEmpty(cli.ParticlesM))
                    throw new ArgumentException("Exactly one of --particles_relion and --particles_m must be provided.");
                if (!File.Exists(cli.ParticlesRelion ?? cli.ParticlesM))
                    throw new FileNotFoundException("Particle table not found.", cli.ParticlesRelion ?? cli.ParticlesM);
                if (cli.Tolerance < 0)
                    throw new ArgumentException("--tolerance must not be negative.");
                if (!cli.KeepOnlyOld && !cli.KeepInBoth && !cli.KeepOnlyNew)
                    throw new ArgumentException("Specify at least one of --keep_only_old, --keep_in_both, or --keep_only_new.");

                Population population = new Population(cli.Population);
                Species species = ResolveSpecies(population, cli.Species);
                Particle[] imported;
                int unmatched;
                if (!string.IsNullOrEmpty(cli.ParticlesM))
                    imported = ParseM(cli.ParticlesM, species, population, out unmatched);
                else
                    imported = ParseRelion(cli, species, population, out unmatched);
                if (unmatched > 0 && !cli.IgnoreUnmatched)
                    throw new InvalidOperationException($"{unmatched} imported particles could not be matched to a data source. Use --ignore_unmatched to continue.");

                Particle[] oldParticles = species.Particles ?? Array.Empty<Particle>();
                ParticleSetIntersection.Result matching = ParticleSetIntersection.Match(oldParticles, imported);
                Particle[] selected = ParticleSetIntersection.Select(oldParticles, imported, matching, cli.Tolerance,
                    cli.KeepOnlyOld, cli.KeepInBoth, cli.KeepOnlyNew);
                int inBoth = matching.Matches.Count(m => ParticleSetIntersection.IsWithinTolerance(m.Distance, cli.Tolerance));
                Console.WriteLine($"Existing particles: {oldParticles.Length}");
                Console.WriteLine($"Imported particles: {imported.Length}");
                Console.WriteLine($"Only old: {oldParticles.Length - inBoth}");
                Console.WriteLine($"In both: {inBoth}");
                Console.WriteLine($"Only new: {imported.Length - inBoth}");
                Console.WriteLine($"Selected: {selected.Length}");

                if (!cli.Apply)
                {
                    Console.WriteLine("Report only; use --apply to replace the species particle set.");
                    return;
                }
                if (selected.Length == 0)
                    throw new InvalidOperationException("Refusing to apply an empty particle selection.");

                species.ReplaceParticles(selected);
                species.CalculateParticleStats();
                species.Commit();
                species.Save();
                Console.WriteLine("Applied selected particles.");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("ERROR: " + exception.Message);
            }
        }

        private static Particle[] ParseM(string path, Species species, Population population, out int unmatched)
        {
            Star table = new Star(path);
            string[] required = { "wrpCoordinateX1", "wrpCoordinateY1", "wrpAngleRot1", "wrpAngleTilt1", "wrpAnglePsi1", "wrpSourceHash" };
            if (required.Any(column => !table.HasColumn(column)))
                throw new InvalidOperationException("M particle table lacks required coordinate, angle, or wrpSourceHash columns.");

            HashSet<string> available = new HashSet<string>(population.Sources.SelectMany(source => source.Files.Keys));
            List<int> validRows = Enumerable.Range(0, table.RowCount).Where(i => available.Contains(table.GetRowValue(i, "wrpSourceHash"))).ToList();
            unmatched = table.RowCount - validRows.Count;
            table = table.CreateSubset(validRows);
            int movementSamples = CountSamples(table, "wrpCoordinateX", "wrpCoordinateY", "wrpCoordinateZ");
            int rotationSamples = CountSamples(table, "wrpAngleRot", "wrpAngleTilt", "wrpAnglePsi");
            Particle[] result = new Particle[table.RowCount];
            for (int row = 0; row < result.Length; row++)
            {
                float3[] coordinates = Enumerable.Range(1, movementSamples).Select(i => new float3(Parse(table, row, "wrpCoordinateX" + i), Parse(table, row, "wrpCoordinateY" + i), Parse(table, row, "wrpCoordinateZ" + i))).ToArray();
                float3[] angles = Enumerable.Range(1, rotationSamples).Select(i => new float3(Parse(table, row, "wrpAngleRot" + i), Parse(table, row, "wrpAngleTilt" + i), Parse(table, row, "wrpAnglePsi" + i))).ToArray();
                result[row] = new Particle(coordinates, angles, int.Parse(table.GetRowValue(row, "wrpRandomSubset"), CultureInfo.InvariantCulture) - 1,
                    table.GetRowValue(row, "wrpSourceName"), table.GetRowValue(row, "wrpSourceHash"));
                result[row].ResampleCoordinates(species.TemporalResolutionMovement);
                result[row].ResampleAngles(species.TemporalResolutionRotation);
            }
            Species.AttachExtraColumns(result, table, Species.IsReservedParticleColumn);
            return result;
        }

        private static Particle[] ParseRelion(IntersectParticlesOptions cli, Species species, Population population, out int unmatched)
        {
            (Star table, bool isRelion3) = Star.LoadRelion3Particles(cli.ParticlesRelion);
            Star optics = null;
            try { optics = new Star(cli.ParticlesRelion, "optics"); } catch { }
            string nameColumn = table.HasColumn("rlnMicrographName") ? "rlnMicrographName" : "rlnTomoName";
            string[] required = { "rlnCoordinateX", "rlnCoordinateY", "rlnAngleRot", "rlnAngleTilt", "rlnAnglePsi", nameColumn };
            if (required.Any(column => !table.HasColumn(column)))
                throw new InvalidOperationException("RELION particle table lacks required coordinate, angle, or micrograph/tomogram columns.");

            Dictionary<string, string> sources = BuildSourceNames(population, cli.Mapping);
            float coordinateAngpix = GetCoordinateAngpix(table, optics, cli.AngPixRelionPos);
            float shiftAngpix = cli.AngPixRelionShifts ?? ((isRelion3 || table.HasColumn("rlnOriginXAngst")) ? 1 : coordinateAngpix);
            List<int> validRows = new List<int>();
            List<string> hashes = new List<string>();
            for (int row = 0; row < table.RowCount; row++)
            {
                string name = Helper.PathToNameWithExtension(table.GetRowValue(row, nameColumn));
                if (sources.TryGetValue(name, out string hash)) { validRows.Add(row); hashes.Add(hash); }
            }
            unmatched = table.RowCount - validRows.Count;
            Star clean = table.CreateSubset(validRows);
            Particle[] result = new Particle[clean.RowCount];
            for (int row = 0; row < result.Length; row++)
            {
                float x = Parse(clean, row, "rlnCoordinateX") * coordinateAngpix - Origin(clean, row, "X", shiftAngpix);
                float y = Parse(clean, row, "rlnCoordinateY") * coordinateAngpix - Origin(clean, row, "Y", shiftAngpix);
                float z = clean.HasColumn("rlnCoordinateZ") ? Parse(clean, row, "rlnCoordinateZ") * coordinateAngpix - Origin(clean, row, "Z", shiftAngpix) : 0;
                int subset = clean.HasColumn("rlnRandomSubset") ? int.Parse(clean.GetRowValue(row, "rlnRandomSubset"), CultureInfo.InvariantCulture) - 1 : row % 2;
                result[row] = new Particle([new float3(x, y, z)], [new float3(Parse(clean, row, "rlnAngleRot"), Parse(clean, row, "rlnAngleTilt"), Parse(clean, row, "rlnAnglePsi"))], subset,
                    clean.GetRowValue(row, nameColumn), hashes[row]);
                result[row].ResampleCoordinates(species.TemporalResolutionMovement);
                result[row].ResampleAngles(species.TemporalResolutionRotation);
            }
            Species.AttachExtraColumns(result, clean, name => name.StartsWith("rln", StringComparison.Ordinal));
            return result;
        }

        private static Dictionary<string, string> BuildSourceNames(Population population, string mappingPath)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            void Add(string name, string hash)
            {
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(hash)) return;
                string key = Helper.PathToNameWithExtension(name);
                if (result.TryGetValue(key, out string existing) && existing != hash)
                    throw new InvalidOperationException($"Source name '{key}' is ambiguous between source hashes.");
                result[key] = hash;
            }
            if (!string.IsNullOrWhiteSpace(mappingPath))
            {
                MappingFile mapping = JsonSerializer.Deserialize<MappingFile>(File.ReadAllText(mappingPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (mapping?.particles == null) throw new InvalidOperationException("Mapping JSON does not contain particles.");
                foreach (MappingParticle particle in mapping.particles)
                {
                    Add(particle.output_name, particle.source_hash);
                    // export_subtomos writes rlnTomoName as output_name + ".tomostar".
                    if (!string.IsNullOrWhiteSpace(particle.output_name) && !particle.output_name.EndsWith(".tomostar", StringComparison.OrdinalIgnoreCase))
                        Add(particle.output_name + ".tomostar", particle.source_hash);
                }
            }
            else
                foreach (DataSource source in population.Sources)
                    foreach (var file in source.Files) Add(file.Value, file.Key);
            return result;
        }

        private static int CountSamples(Star table, params string[] prefixes)
        {
            int samples = 1;
            while (prefixes.All(prefix => table.HasColumn(prefix + (samples + 1)))) samples++;
            return samples;
        }

        private static float GetCoordinateAngpix(Star table, Star optics, float? overrideValue)
        {
            if (overrideValue is > 0) return overrideValue.Value;
            if (table.HasColumn("rlnDetectorPixelSize") && table.HasColumn("rlnMagnification"))
                return Parse(table, 0, "rlnDetectorPixelSize") * 1e4f / Parse(table, 0, "rlnMagnification");
            if (table.HasColumn("rlnImagePixelSize")) return Parse(table, 0, "rlnImagePixelSize");
            if (optics != null && optics.HasColumn("rlnImagePixelSize")) return Parse(optics, 0, "rlnImagePixelSize");
            throw new InvalidOperationException("Couldn't determine RELION coordinate pixel size; specify --angpix_coords.");
        }

        private static float Origin(Star table, int row, string axis, float pixelSize) => table.HasColumn("rlnOrigin" + axis + "Angst") ? Parse(table, row, "rlnOrigin" + axis + "Angst") : table.HasColumn("rlnOrigin" + axis) ? Parse(table, row, "rlnOrigin" + axis) * pixelSize : 0;
        private static float Parse(Star table, int row, string column) => float.Parse(table.GetRowValue(row, column), CultureInfo.InvariantCulture);

        private static Species ResolveSpecies(Population population, string selector)
        {
            if (File.Exists(selector))
                return Species.FromFile(selector);

            Species[] all = population.Species.SelectMany(s => s.AllDescendants).ToArray();
            string selectorPath = Path.GetFullPath(selector);
            Species[] matches = all.Where(s => s.Name == selector || s.GUID.ToString().Equals(selector, StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(s.Path) == selectorPath).ToArray();
            if (matches.Length == 1) return matches[0];
            if (matches.Length == 0) throw new InvalidOperationException($"No species matched '{selector}'.");
            throw new InvalidOperationException($"More than one species matched '{selector}'.");
        }
    }
}
