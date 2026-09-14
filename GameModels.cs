using System.Drawing;
using System.Text.Json.Serialization;

namespace DoodleRoadWorkshop;

internal enum ScreenMode { Menu, Levels, Garage, Game }
internal enum RunPhase { Build, Drive, Paused, Won, Lost }
internal enum BuildTool { Road, Beam, Wheel, Balloon, Rocket, Eraser }
internal enum PartType { Wheel, Balloon, Rocket }
internal enum ObstacleType { Rock, Crate, Spikes, Saw, Mud, Wind, Wall }

internal readonly record struct V2(float X, float Y)
{
    public static V2 operator +(V2 a, V2 b) => new(a.X + b.X, a.Y + b.Y);
    public static V2 operator -(V2 a, V2 b) => new(a.X - b.X, a.Y - b.Y);
    public static V2 operator *(V2 a, float b) => new(a.X * b, a.Y * b);
    public float Length => MathF.Sqrt(X * X + Y * Y);
    public static float Distance(V2 a, V2 b) => (a - b).Length;
    public PointF Point => new(X, Y);
    public V2 Rotate(float a)
    {
        float c = MathF.Cos(a), s = MathF.Sin(a);
        return new V2(X * c - Y * s, X * s + Y * c);
    }
}

internal sealed class RoadStroke
{
    public List<V2> Points { get; } = new();
    public bool Beam { get; init; }
    public float Length
    {
        get
        {
            float n = 0;
            for (int i = 1; i < Points.Count; i++) n += V2.Distance(Points[i - 1], Points[i]);
            return n;
        }
    }
}

internal sealed class VehiclePart
{
    public PartType Type { get; init; }
    public V2 Offset { get; init; }
    public float Rotation { get; init; }
}

internal sealed class Obstacle
{
    public ObstacleType Type { get; init; }
    public float X { get; init; }
    public float Y { get; set; }
    public float BaseY { get; init; }
    public float Phase { get; init; }
    public bool Broken { get; set; }
}

internal sealed class Pickup
{
    public float X { get; init; }
    public float Y { get; init; }
    public bool Taken { get; set; }
}

internal sealed class TerrainSegment
{
    public V2 A { get; init; }
    public V2 B { get; init; }
}

internal sealed class GeneratedLevel
{
    public int Number { get; init; }
    public int Seed { get; init; }
    public float Width { get; init; }
    public List<TerrainSegment> Terrain { get; } = new();
    public List<Obstacle> Obstacles { get; } = new();
    public List<Pickup> Pickups { get; } = new();
    public float GoalX => Width - 170;
}

internal sealed class Vehicle
{
    public float X;
    public float Y;
    public float Vx;
    public float Vy;
    public float Angle;
    public float AngularVelocity;
    public float Fuel;
    public int Damage;
    public int Coins;
    public bool Grounded;
    public float CollisionCooldown;
    public float TrailTimer;

    public void Reset(float fuel)
    {
        X = 125;
        Y = 390;
        Vx = Vy = Angle = AngularVelocity = 0;
        Fuel = fuel;
        Damage = Coins = 0;
        Grounded = false;
        CollisionCooldown = 0;
        TrailTimer = 0;
    }
}

internal sealed class DifficultySpec
{
    public string Name { get; init; } = "";
    public float Gravity { get; init; }
    public float Fuel { get; init; }
    public float Ink { get; init; }
    public int Parts { get; init; }
    public int Obstacles { get; init; }
    public int Gaps { get; init; }
    public float Engine { get; init; }

    public static DifficultySpec For(int index) => index switch
    {
        0 => new() { Name = "ЛЕГКО", Gravity = .31f, Fuel = 150, Ink = 1050, Parts = 14, Obstacles = 4, Gaps = 3, Engine = .105f },
        2 => new() { Name = "СЛОЖНО", Gravity = .47f, Fuel = 88, Ink = 600, Parts = 7, Obstacles = 10, Gaps = 7, Engine = .082f },
        _ => new() { Name = "НОРМАЛЬНО", Gravity = .39f, Fuel = 115, Ink = 790, Parts = 10, Obstacles = 7, Gaps = 5, Engine = .092f }
    };
}

internal sealed class SaveData
{
    public int Version { get; set; } = 1;
    public int UnlockedLevel { get; set; } = 1;
    public int TotalStars { get; set; }
    public int Difficulty { get; set; } = 1;
    public bool Sound { get; set; } = true;
    public int BodyColor { get; set; }
    public int BodyStyle { get; set; }
    public int WheelStyle { get; set; }
    public int TrailStyle { get; set; }
    public int CampaignSeed { get; set; } = Random.Shared.Next(100000, 999999);
    public Dictionary<int, int> LevelStars { get; set; } = new();
}

internal sealed class TrailDot
{
    public V2 Position { get; init; }
    public float Life { get; set; }
    public Color Color { get; init; }
}
