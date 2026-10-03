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

`--apply` refuses an empty selection. On success it calls `ReplaceParticles`,
`CalculateParticleStats`, `Commit`, and `Save` on the selected species.
