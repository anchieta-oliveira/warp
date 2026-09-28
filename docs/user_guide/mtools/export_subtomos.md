# Export M-refined subtomograms

`MTools export_subtomos` exports new subtomogram and CTF volumes from the particle
trajectories saved in a refined M species:

```bash
MTools export_subtomos \
  --population /data/project/M/population.population \
  --species ribosome \
  --output /data/project/M/export_relion/particles_Mrefined.star \
  --device_list 0 1 \
  --prerotate
```

Use `--dry_run` to validate the population, species, source hashes, output paths and
GPUs without reconstructing volumes.

This command is not equivalent to `WarpTools ts_export_particles`. It loads the refined
`Species.Particles` and samples every particle with `GetCoordinateSeries()` and
`GetAngleSeries()` at the tilt-series dose positions. It then dispatches the native
`TiltSeries.ReconstructSubtomos` command through `WarpWorker2`.

The output STAR and `<prefix>_mapping.json` are placed beside `--output`. Native Warp
subtomogram and CTF volumes are written under each tilt-series `subtomo/<series>/`
directory. Existing outputs are rejected unless `--overwrite` is supplied.
