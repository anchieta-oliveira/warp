# Export M-refined particles

`MTools export_subtomos` exports new subtomogram and CTF volumes from the particle
trajectories saved in a refined M species:

```bash
MTools export_subtomos \
  --population /data/project/M/population.population \
  --species ribosome \
  --output /data/project/M/export_relion/particles_Mrefined.star \
  --subtomo_dir /data/project/M/export_relion/subtomograms \
  --device_list 0 1 \
  --prerotate
```

Use `--dry_run` to validate the population, species, source hashes, output paths and
GPUs without reconstructing volumes.

This command is not equivalent to `WarpTools ts_export_particles`. It loads the refined
`Species.Particles` and samples every particle with `GetCoordinateSeries()` and
`GetAngleSeries()` at the tilt-series dose positions. It then dispatches the native
`TiltSeries.ReconstructSubtomos` command through `WarpWorker2`.

The output STAR and `<prefix>_mapping.json` are placed beside `--output`. By default,
native subtomogram and CTF volumes are written under each tilt-series
`subtomo/<series>/` directory. Set `--subtomo_dir` to write them instead under
`<subtomo_dir>/<series>/`. Existing outputs are rejected unless `--overwrite` is
supplied.

## 2D particle series

Add `--2d` to export the refined trajectories as RELION 2D particle series instead
of 3D subtomograms:

```bash
MTools export_subtomos \
  --population /data/project/M/population.population \
  --species ribosome \
  --output /data/project/M/export_relion/particles_Mrefined.star \
  --2d \
  --particle_series_dir /data/project/M/export_relion/particle_series \
  --max_missing_tilts 5
```

Particle stacks are written below `<particle_series_dir>/<series>/` (or
`<directory-containing-output>/particle_series/<series>/` when omitted). The command writes
the particle STAR (`--output`), `<prefix>_tomograms.star`,
`<prefix>_optimisation_set.star`, and `dummy_tiltseries.mrc` beside `--output`.
Particles with no visible tilts or more than `--max_missing_tilts` missing selected
tilts are omitted from the final particle STAR. Random subsets and species extra
columns are retained. `--subtomo_dir`, `--prerotate`, `--make_sparse`, and
`--dont_normalize_output` are 3D-only; `--particle_series_dir`,
`--dont_premultiply`, and non-default `--max_missing_tilts` are 2D-only.

When a merged population contains different tilt series with the same root name,
the exporter appends the source SHA-1 to their output directory and RELION tomogram
name. Unique root names retain their existing paths.
