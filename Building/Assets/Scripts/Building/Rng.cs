using System.Collections.Generic;

namespace Drone2.Building
{
    /// Seeded random source so one seed always rebuilds the same building.
    public class Rng
    {
        readonly System.Random random;

        public Rng(int seed) => random = new System.Random(seed);

        /// Integer in [min, max).
        public int Range(int min, int max) => max <= min ? min : random.Next(min, max);

        public float Range(float min, float max) => min + (float)random.NextDouble() * (max - min);

        public bool Chance(float p) => random.NextDouble() < p;

        public T PickFrom<T>(IList<T> items) => items[random.Next(items.Count)];

        public T Pick<T>(params T[] items) => items[random.Next(items.Length)];

        public void Shuffle<T>(IList<T> items)
        {
            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (items[i], items[j]) = (items[j], items[i]);
            }
        }

        /// Picks an index with probability proportional to its weight.
        public int Weighted(IList<float> weights)
        {
            float total = 0;
            foreach (var w in weights) total += w;
            float r = Range(0f, total);
            for (int i = 0; i < weights.Count; i++)
            {
                r -= weights[i];
                if (r <= 0) return i;
            }
            return weights.Count - 1;
        }
    }
}
