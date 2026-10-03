# `intersect_particles`

Selects a species' particles by matching an imported M or RELION STAR table to the
existing particle set. Matching is performed independently for each `SourceHash`
with a minimum-total-distance Hungarian assignment. Coordinates are compared in
Angstrom using the first pose sample. Particles in both sets retain the original
species particle objects and therefore their refined trajectories.

```text
MTools intersect_particles --population project.population --species SpeciesName \
  --particles_relion particles.star --keep_in_both --tolerance 10 --apply
```

Exactly one of `--particles_relion` and `--particles_m` is required. At least one
of `--keep_only_old`, `--keep_in_both`, and `--keep_only_new` must be specified.
Without `--apply`, the command only prints the old-only, in-both, new-only, and
selected counts.

RELION coordinates and origins use the pixel-size behavior of `create_species`.
Use `--angpix_coords` and `--angpix_shifts` to override detected values. RELION
source names normally resolve against the population; for 2D tomographic output
from `export_subtomos`, pass its `_mapping.json` with `--mapping` to resolve each
`output_name` to its source hash. Unresolvable input rows fail by default; use
`--ignore_unmatched` to omit them.

`--apply` refuses an empty selection. Without `--new_name`, it calls
`ReplaceParticles`, `CalculateParticleStats`, `Commit`, and `Save` on the
selected species.

Pass `--new_name NAME` to plan a new independent top-level species; add `--apply`
to create it and register it in the same population while leaving the input species
unchanged. By default its path is
`<Population.SpeciesDir>/<NameSafe>_<GUID-8>/<NameSafe>.species`; use
`--output_species PATH` to choose a different new destination (it requires
`--new_name`). Existing species names and destinations are rejected, as is a
destination under the original species folder. The new species has a new GUID and
no version history, retains the original settings, used data sources, maps, mask,
and denoiser artifact, and writes the selected particle STAR plus fresh particle
statistics without recalculating map resolution or training a denoiser.
