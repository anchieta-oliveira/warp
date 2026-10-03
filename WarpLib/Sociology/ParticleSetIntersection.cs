using System;
using System.Collections.Generic;
using System.Linq;
using Warp.Tools;

namespace Warp.Sociology
{
    /// <summary>Matches particle sets independently for each source hash.</summary>
    public static class ParticleSetIntersection
    {
        public sealed class ParticleMatch
        {
            public int OldIndex { get; init; }
            public int NewIndex { get; init; }
            public float Distance { get; init; }
        }

        public sealed class Result
        {
            public float[] OldDistances { get; init; }
            public float[] NewDistances { get; init; }
            public ParticleMatch[] Matches { get; init; }
        }

        public static Result Match(Particle[] oldParticles, Particle[] newParticles)
        {
            if (oldParticles == null)
                throw new ArgumentNullException(nameof(oldParticles));
            if (newParticles == null)
                throw new ArgumentNullException(nameof(newParticles));

            float[] oldDistances = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Repeat(-1f, oldParticles.Length));
            float[] newDistances = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Repeat(-1f, newParticles.Length));
            List<ParticleMatch> matches = new List<ParticleMatch>();

            Dictionary<string, List<int>> oldBySource = GroupBySource(oldParticles);
            Dictionary<string, List<int>> newBySource = GroupBySource(newParticles);
            foreach (var group in newBySource)
            {
                if (!oldBySource.TryGetValue(group.Key, out List<int> oldIndices))
                    continue;

                List<int> newIndices = group.Value;
                float[][] distances = new float[oldIndices.Count][];
                for (int oldLocal = 0; oldLocal < oldIndices.Count; oldLocal++)
                {
                    distances[oldLocal] = new float[newIndices.Count];
                    float3 oldCoordinate = oldParticles[oldIndices[oldLocal]].Coordinates[0];
                    for (int newLocal = 0; newLocal < newIndices.Count; newLocal++)
                        distances[oldLocal][newLocal] = (newParticles[newIndices[newLocal]].Coordinates[0] - oldCoordinate).Length();
                }

                int[] assignment = new HungarianAlgorithm(distances).execute();
                for (int oldLocal = 0; oldLocal < assignment.Length; oldLocal++)
                {
                    int newLocal = assignment[oldLocal];
                    if (newLocal < 0)
                        continue;

                    int oldIndex = oldIndices[oldLocal];
                    int newIndex = newIndices[newLocal];
                    float distance = distances[oldLocal][newLocal];
                    oldDistances[oldIndex] = distance;
                    newDistances[newIndex] = distance;
                    matches.Add(new ParticleMatch { OldIndex = oldIndex, NewIndex = newIndex, Distance = distance });
                }
            }

            return new Result { OldDistances = oldDistances, NewDistances = newDistances, Matches = matches.ToArray() };
        }

        /// <summary>Selects particles without copying old particles, preserving their refined trajectories.</summary>
        public static Particle[] Select(Particle[] oldParticles, Particle[] newParticles, Result match,
                                        float tolerance, bool keepOnlyOld, bool keepInBoth, bool keepOnlyNew)
        {
            if (match == null)
                throw new ArgumentNullException(nameof(match));
            if (tolerance < 0)
                throw new ArgumentOutOfRangeException(nameof(tolerance));

            List<Particle> selected = new List<Particle>();
            for (int i = 0; i < oldParticles.Length; i++)
            {
                bool inBoth = IsWithinTolerance(match.OldDistances[i], tolerance);
                if ((inBoth && keepInBoth) || (!inBoth && keepOnlyOld))
                    selected.Add(oldParticles[i]);
            }
            for (int i = 0; i < newParticles.Length; i++)
                if (!IsWithinTolerance(match.NewDistances[i], tolerance) && keepOnlyNew)
                    selected.Add(newParticles[i]);

            return System.Linq.Enumerable.ToArray(System.Linq.Enumerable.OrderBy(selected, p => p.SourceHash, StringComparer.Ordinal));
        }

        public static bool IsWithinTolerance(float distance, float tolerance) => distance >= 0 && distance <= tolerance + 1e-6f;

        private static Dictionary<string, List<int>> GroupBySource(Particle[] particles)
        {
            Dictionary<string, List<int>> result = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < particles.Length; i++)
            {
                string sourceHash = particles[i].SourceHash ?? string.Empty;
                if (!result.TryGetValue(sourceHash, out List<int> indices))
                    result.Add(sourceHash, indices = new List<int>());
                indices.Add(i);
            }
            return result;
        }
    }
}
