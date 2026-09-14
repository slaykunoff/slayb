namespace DoodleRoadWorkshop;

internal static class LevelGenerator
{
    public static GeneratedLevel Generate(int number, int difficulty, int campaignSeed, int variant)
    {
        var spec = DifficultySpec.For(difficulty);
        int seed = HashCode.Combine(campaignSeed, number * 7919, difficulty * 104729, variant * 17);
        var random = new Random(seed);
        float width = (difficulty switch { 0 => 2850, 2 => 4100, _ => 3450 }) + Math.Min(900, number * 9);
        var level = new GeneratedLevel { Number = number, Seed = seed, Width = width };

        const float step = 115;
        int count = (int)MathF.Ceiling(width / step) + 1;
        var points = new List<V2>(count);
        float y = 535;
        for (int i = 0; i < count; i++)
        {
            float x = Math.Min(width, i * step);
            if (x < 480 || x > width - 420) y = 535;
            else
            {
                y += random.Next(-52, 53);
                y = Math.Clamp(y, 390, 600);
                if (random.NextDouble() < .15) y += random.Next(-35, 36);
            }
            points.Add(new V2(x, y));
        }

        var gapSegments = new HashSet<int>();
        int wantedGaps = Math.Min(spec.Gaps + number / 18, Math.Max(1, count / 7));
        int guard = 0;
        while (gapSegments.Count < wantedGaps && guard++ < 1000)
        {
            int i = random.Next(5, count - 5);
            bool near = gapSegments.Any(g => Math.Abs(g - i) < 3);
            if (!near)
            {
                gapSegments.Add(i);
                if (difficulty == 2 && random.NextDouble() < .35 && i + 1 < count - 4) gapSegments.Add(i + 1);
            }
        }

        for (int i = 0; i < points.Count - 1; i++)
        {
            if (gapSegments.Contains(i)) continue;
            level.Terrain.Add(new TerrainSegment { A = points[i], B = points[i + 1] });
        }

        int obstacleCount = spec.Obstacles + Math.Min(5, number / 12);
        guard = 0;
        while (level.Obstacles.Count < obstacleCount && guard++ < 1000)
        {
            float x = random.Next(620, Math.Max(621, (int)width - 420));
            float? surface = SurfaceAt(level.Terrain, x);
            if (surface is null || level.Obstacles.Any(o => Math.Abs(o.X - x) < 155)) continue;
            int roll = random.Next(100);
            ObstacleType type = roll switch
            {
                < 21 => ObstacleType.Rock,
                < 38 => ObstacleType.Crate,
                < 56 => ObstacleType.Spikes,
                < 70 => ObstacleType.Saw,
                < 82 => ObstacleType.Mud,
                < 92 => ObstacleType.Wind,
                _ => ObstacleType.Wall
            };
            float oy = surface.Value;
            level.Obstacles.Add(new Obstacle
            {
                Type = type,
                X = x,
                Y = oy,
                BaseY = oy,
                Phase = (float)(random.NextDouble() * Math.PI * 2)
            });
        }

        for (float x = 500; x < width - 300; x += random.Next(240, 390))
        {
            float? surface = SurfaceAt(level.Terrain, x);
            if (surface is not null)
                level.Pickups.Add(new Pickup { X = x, Y = surface.Value - random.Next(75, 145) });
        }

        return level;
    }

    public static float? SurfaceAt(IEnumerable<TerrainSegment> segments, float x)
    {
        float? best = null;
        foreach (var s in segments)
        {
            float min = Math.Min(s.A.X, s.B.X), max = Math.Max(s.A.X, s.B.X);
            if (x < min || x > max || max - min < .01f) continue;
            float t = (x - s.A.X) / (s.B.X - s.A.X);
            float y = s.A.Y + (s.B.Y - s.A.Y) * t;
            if (best is null || y < best) best = y;
        }
        return best;
    }
}
