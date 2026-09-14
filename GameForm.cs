using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json;

namespace DoodleRoadWorkshop;

internal sealed class GameForm : Form
{
    private const float W = 1280;
    private const float H = 720;
    private const int MaxLevels = 200;

    private readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly HashSet<Keys> keys = new();
    private readonly Vehicle car = new();
    private readonly List<RoadStroke> roads = new();
    private readonly List<VehiclePart> parts = new();
    private readonly List<TrailDot> trail = new();
    private readonly SoundBank sound = new();

    private SaveData save = new();
    private ScreenMode screen = ScreenMode.Menu;
    private RunPhase phase = RunPhase.Build;
    private BuildTool tool = BuildTool.Road;
    private GeneratedLevel? level;
    private RoadStroke? drawingStroke;
    private V2? beamStart;
    private PointF mouse;
    private float scale = 1;
    private float offsetX;
    private float offsetY;
    private float camera;
    private float inkLeft;
    private int partsLeft;
    private int currentLevel = 1;
    private int levelPage;
    private int reroll;
    private int resultStars;
    private float runTime;
    private float stuckTime;
    private float shake;
    private float toastTime;
    private string toast = "";
    private bool draggingCamera;
    private float dragStartX;
    private float cameraStart;
    private long lastTick;
    private readonly string savePath = Path.Combine(AppContext.BaseDirectory, "save.json");

    private static readonly Color Paper = Color.FromArgb(250, 246, 236);
    private static readonly Color Ink = Color.FromArgb(28, 31, 39);
    private static readonly Color Green = Color.FromArgb(36, 196, 105);
    private static readonly Color Blue = Color.FromArgb(61, 127, 245);
    private static readonly Color Red = Color.FromArgb(239, 61, 68);
    private static readonly Color Gold = Color.FromArgb(255, 191, 35);
    private static readonly Color[] CarColors =
    {
        Color.FromArgb(235, 47, 55), Color.FromArgb(50, 125, 238),
        Color.FromArgb(43, 190, 103), Color.FromArgb(252, 153, 35),
        Color.FromArgb(157, 88, 220), Color.FromArgb(245, 92, 164),
        Color.FromArgb(44, 48, 57), Color.FromArgb(240, 218, 73)
    };

    public GameForm()
    {
        Text = "Doodle Road Workshop";
        ClientSize = new Size((int)W, (int)H);
        MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = Paper;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        LoadSave();
        sound.Enabled = save.Sound;
        timer.Tick += OnTick;
        timer.Start();
        lastTick = clock.ElapsedMilliseconds;

        Paint += OnPaintGame;
        MouseDown += OnMouseDownGame;
        MouseMove += OnMouseMoveGame;
        MouseUp += OnMouseUpGame;
        MouseWheel += OnMouseWheelGame;
        KeyDown += OnKeyDownGame;
        KeyUp += (_, e) => keys.Remove(e.KeyCode);
        FormClosing += (_, _) => SaveGame();
    }

    private void LoadSave()
    {
        try
        {
            if (File.Exists(savePath))
                save = JsonSerializer.Deserialize<SaveData>(File.ReadAllText(savePath)) ?? new SaveData();
            save.UnlockedLevel = Math.Clamp(save.UnlockedLevel, 1, MaxLevels);
            save.Difficulty = Math.Clamp(save.Difficulty, 0, 2);
        }
        catch
        {
            save = new SaveData();
        }
    }

    private void SaveGame()
    {
        try
        {
            string json = JsonSerializer.Serialize(save, new JsonSerializerOptions { WriteIndented = true });
            string temp = savePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, savePath, true);
        }
        catch
        {
            ShowToast("Не удалось записать save.json рядом с игрой", 4);
        }
    }

    private void ShowToast(string text, float seconds = 2)
    {
        toast = text;
        toastTime = seconds;
    }

    private void NewLevel(int number)
    {
        currentLevel = Math.Clamp(number, 1, MaxLevels);
        reroll = 0;
        BuildLevel();
    }

    private void BuildLevel()
    {
        level = LevelGenerator.Generate(currentLevel, save.Difficulty, save.CampaignSeed, reroll);
        var spec = DifficultySpec.For(save.Difficulty);
        inkLeft = spec.Ink;
        partsLeft = spec.Parts;
        roads.Clear();
        parts.Clear();
        trail.Clear();
        car.Reset(spec.Fuel);
        phase = RunPhase.Build;
        camera = 0;
        runTime = stuckTime = 0;
        resultStars = 0;
        tool = BuildTool.Road;
        screen = ScreenMode.Game;
        sound.Play("click");
        Invalidate();
    }

    private void StartDrive()
    {
        if (level is null) return;
        phase = RunPhase.Drive;
        car.Reset(DifficultySpec.For(save.Difficulty).Fuel);
        trail.Clear();
        camera = 0;
        runTime = stuckTime = 0;
        sound.Play("start");
    }

    private void BackToBuild()
    {
        if (level is null) return;
        phase = RunPhase.Build;
        car.Reset(DifficultySpec.For(save.Difficulty).Fuel);
        foreach (var p in level.Pickups) p.Taken = false;
        foreach (var o in level.Obstacles) o.Broken = false;
        trail.Clear();
        camera = Math.Clamp(car.X - 250, 0, Math.Max(0, level.Width - W));
        runTime = stuckTime = 0;
    }

    private void Finish(bool won)
    {
        if (phase is RunPhase.Won or RunPhase.Lost) return;
        phase = won ? RunPhase.Won : RunPhase.Lost;
        if (won)
        {
            int totalCoins = Math.Max(1, level?.Pickups.Count ?? 1);
            resultStars = 1;
            if (car.Damage == 0) resultStars++;
            if (car.Coins >= Math.Ceiling(totalCoins * .6) || car.Fuel > 28) resultStars++;
            resultStars = Math.Min(3, resultStars);

            int old = save.LevelStars.TryGetValue(currentLevel, out int s) ? s : 0;
            if (resultStars > old)
            {
                save.LevelStars[currentLevel] = resultStars;
                save.TotalStars += resultStars - old;
            }
            save.UnlockedLevel = Math.Max(save.UnlockedLevel, Math.Min(MaxLevels, currentLevel + 1));
            SaveGame();
            sound.Play("win");
        }
        else
        {
            resultStars = 0;
            sound.Play("fail");
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        long now = clock.ElapsedMilliseconds;
        float dt = Math.Clamp((now - lastTick) / 16.6667f, .2f, 2.5f);
        lastTick = now;

        if (toastTime > 0) toastTime -= dt / 60;
        if (shake > 0) shake = Math.Max(0, shake - dt);

        if (screen == ScreenMode.Game && level is not null)
        {
            float seconds = dt / 60f;
            foreach (var o in level.Obstacles)
                if (o.Type == ObstacleType.Saw)
                    o.Y = o.BaseY - 82 + MathF.Sin((float)clock.Elapsed.TotalSeconds * 2.1f + o.Phase) * 46;

            if (phase == RunPhase.Build)
            {
                float pan = 0;
                if (keys.Contains(Keys.A) || keys.Contains(Keys.Left)) pan -= 12 * dt;
                if (keys.Contains(Keys.D) || keys.Contains(Keys.Right)) pan += 12 * dt;
                camera = Math.Clamp(camera + pan, 0, Math.Max(0, level.Width - W));
            }
            else if (phase == RunPhase.Drive)
            {
                UpdateVehicle(dt);
                camera += (Math.Clamp(car.X - 330, 0, Math.Max(0, level.Width - W)) - camera) * .07f * dt;
                runTime += seconds;
            }
        }

        for (int i = trail.Count - 1; i >= 0; i--)
        {
            trail[i].Life -= dt / 60;
            if (trail[i].Life <= 0) trail.RemoveAt(i);
        }
        Invalidate();
    }

    private void UpdateVehicle(float dt)
    {
        if (level is null) return;
        var spec = DifficultySpec.For(save.Difficulty);
        int rockets = parts.Count(p => p.Type == PartType.Rocket);
        int balloons = parts.Count(p => p.Type == PartType.Balloon);
        int extraWheels = parts.Count(p => p.Type == PartType.Wheel);

        bool gas = keys.Contains(Keys.Right) || keys.Contains(Keys.D) || keys.Contains(Keys.W) || keys.Contains(Keys.Up);
        bool brake = keys.Contains(Keys.Left) || keys.Contains(Keys.A) || keys.Contains(Keys.S) || keys.Contains(Keys.Down);
        bool boost = keys.Contains(Keys.Space) && rockets > 0 && car.Fuel > 0;

        float throttle = gas ? 1f : .54f;
        if (brake) throttle = -.48f;
        bool inMud = level.Obstacles.Any(o => o.Type == ObstacleType.Mud && !o.Broken && Math.Abs(o.X - car.X) < 78);

        if (car.Fuel > 0)
        {
            float acceleration = spec.Engine * throttle * (1 + extraWheels * .055f);
            if (inMud) acceleration *= .35f;
            car.Vx += acceleration * dt;
            car.Fuel = Math.Max(0, car.Fuel - (.018f + Math.Abs(throttle) * .017f + (boost ? .085f : 0)) * dt);
        }

        if (boost)
        {
            car.Vx += (.105f + rockets * .035f) * dt;
            car.Vy -= .045f * rockets * dt;
            if (Random.Shared.NextDouble() < .08 * dt) sound.Play("rocket");
        }

        car.Vx = Math.Clamp(car.Vx, -3.2f, boost ? 11.5f : 8.1f);
        car.Vx *= MathF.Pow(inMud ? .975f : (car.Grounded ? .996f : .999f), dt);

        float gravity = spec.Gravity * Math.Max(.28f, 1 - balloons * .13f);
        car.Vy += gravity * dt;
        car.X += car.Vx * dt;
        car.Y += car.Vy * dt;

        float? leftY = SurfaceAt(car.X - 31);
        float? rightY = SurfaceAt(car.X + 31);
        var usable = new List<float>();
        if (leftY.HasValue && leftY.Value > car.Y - 25 && leftY.Value < car.Y + 85) usable.Add(leftY.Value);
        if (rightY.HasValue && rightY.Value > car.Y - 25 && rightY.Value < car.Y + 85) usable.Add(rightY.Value);

        car.Grounded = false;
        if (usable.Count > 0)
        {
            float targetY = usable.Average() - (29 + Math.Min(8, extraWheels * 2));
            if (car.Y >= targetY || car.Y + car.Vy * dt >= targetY)
            {
                float impact = Math.Max(0, car.Vy);
                car.Y = targetY;
                car.Vy = impact > 6 ? -impact * .22f : 0;
                car.Grounded = true;
                float slope = 0;
                if (leftY.HasValue && rightY.HasValue)
                    slope = MathF.Atan2(rightY.Value - leftY.Value, 62);
                car.Angle += (slope - car.Angle) * .14f * dt;
                car.AngularVelocity *= .8f;
                if (impact > 7.2f) Damage("Жёсткое приземление!");
            }
        }
        else
        {
            car.AngularVelocity += (boost ? -.0016f : .00055f) * dt;
            car.Angle += car.AngularVelocity * dt;
        }

        if (balloons > 0 && !car.Grounded)
            car.Vy -= .035f * balloons * dt;

        car.X = Math.Max(35, car.X);
        car.CollisionCooldown = Math.Max(0, car.CollisionCooldown - dt / 60f);
        CheckObstacles(boost);
        CheckPickups();

        car.TrailTimer -= dt;
        if (car.TrailTimer <= 0 && (Math.Abs(car.Vx) > 1 || boost))
        {
            car.TrailTimer = 4;
            Color c = save.TrailStyle switch
            {
                1 => Color.FromArgb(235, 74, 94),
                2 => Color.FromArgb(74, 154, 255),
                3 => Gold,
                _ => Color.FromArgb(120, 90, 75)
            };
            trail.Add(new TrailDot { Position = new V2(car.X - 52, car.Y + 18), Life = .8f, Color = c });
        }

        if (Math.Abs(car.Vx) < .14f && car.Fuel <= 0) stuckTime += dt / 60f; else stuckTime = 0;
        if (car.Y > H + 180 || car.Damage >= 3 || stuckTime > 3.5f || runTime > 150) Finish(false);
        if (car.X >= level.GoalX) Finish(true);
    }

    private void CheckPickups()
    {
        if (level is null) return;
        foreach (var p in level.Pickups)
        {
            if (!p.Taken && V2.Distance(new V2(car.X, car.Y), new V2(p.X, p.Y)) < 48)
            {
                p.Taken = true;
                car.Coins++;
                car.Fuel = Math.Min(DifficultySpec.For(save.Difficulty).Fuel, car.Fuel + 3.5f);
                sound.Play("coin");
            }
        }
    }

    private void CheckObstacles(bool boost)
    {
        if (level is null) return;
        foreach (var o in level.Obstacles)
        {
            if (o.Broken || Math.Abs(o.X - car.X) > 115) continue;
            switch (o.Type)
            {
                case ObstacleType.Mud:
                    break;
                case ObstacleType.Wind:
                    car.Vy -= .12f;
                    car.Vx -= .012f;
                    break;
                case ObstacleType.Saw:
                    if (V2.Distance(new V2(car.X, car.Y), new V2(o.X, o.Y)) < 53) Damage("Осторожно: пила!");
                    break;
                case ObstacleType.Spikes:
                    if (Math.Abs(o.X - car.X) < 43 && Math.Abs((car.Y + 28) - o.BaseY) < 48)
                    {
                        Damage("Шипы!");
                        car.Vy = -4.1f;
                    }
                    break;
                case ObstacleType.Wall:
                    if (Math.Abs(o.X - car.X) < 52 && Math.Abs((car.Y + 20) - o.BaseY) < 78)
                    {
                        if (boost || car.Vx > 7.4f)
                        {
                            o.Broken = true;
                            car.Vx *= .72f;
                            sound.Play("hit");
                        }
                        else
                        {
                            Damage("Стену лучше пробивать ракетой");
                            car.X = o.X - 56;
                            car.Vx = -2.2f;
                        }
                    }
                    break;
                case ObstacleType.Rock:
                case ObstacleType.Crate:
                    if (Math.Abs(o.X - car.X) < 47 && Math.Abs((car.Y + 20) - o.BaseY) < 65)
                    {
                        if (boost && o.Type == ObstacleType.Crate)
                        {
                            o.Broken = true;
                            car.Vx *= .82f;
                            sound.Play("hit");
                        }
                        else
                        {
                            Damage(o.Type == ObstacleType.Rock ? "Камень!" : "Ящик!");
                            car.Vx *= .38f;
                            car.Vy = -2.5f;
                            o.Broken = o.Type == ObstacleType.Crate;
                        }
                    }
                    break;
            }
        }
    }

    private void Damage(string message)
    {
        if (car.CollisionCooldown > 0) return;
        car.CollisionCooldown = .85f;
        car.Damage++;
        shake = 13;
        sound.Play("hit");
        ShowToast(message);
    }

    private float? SurfaceAt(float x)
    {
        if (level is null) return null;
        float? best = LevelGenerator.SurfaceAt(level.Terrain, x);
        foreach (var r in roads)
        {
            for (int i = 1; i < r.Points.Count; i++)
            {
                V2 a = r.Points[i - 1], b = r.Points[i];
                float min = Math.Min(a.X, b.X), max = Math.Max(a.X, b.X);
                if (x < min || x > max || max - min < .01f) continue;
                float t = (x - a.X) / (b.X - a.X);
                float y = a.Y + (b.Y - a.Y) * t;
                if (best is null || y < best) best = y;
            }
        }
        return best;
    }

    private void OnPaintGame(object? sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        scale = Math.Min(ClientSize.Width / W, ClientSize.Height / H);
        offsetX = (ClientSize.Width - W * scale) / 2;
        offsetY = (ClientSize.Height - H * scale) / 2;
        g.TranslateTransform(offsetX, offsetY);
        g.ScaleTransform(scale, scale);

        DrawPaper(g);
        switch (screen)
        {
            case ScreenMode.Menu: DrawMenu(g); break;
            case ScreenMode.Levels: DrawLevels(g); break;
            case ScreenMode.Garage: DrawGarage(g); break;
            case ScreenMode.Game: DrawGame(g); break;
        }

        if (toastTime > 0)
        {
            float alpha = Math.Min(1, toastTime * 2);
            using var b = new SolidBrush(Color.FromArgb((int)(220 * alpha), Ink));
            RoundRect(g, b, null, new RectangleF(390, 645, 500, 48), 15);
            DrawText(g, toast, 18, Color.White, new RectangleF(400, 654, 480, 30), ContentAlignment.MiddleCenter, true);
        }
    }

    private void DrawPaper(Graphics g)
    {
        g.Clear(Paper);
        using var grid = new Pen(Color.FromArgb(24, 173, 147, 110), 1);
        for (int x = 20; x < W; x += 48) g.DrawLine(grid, x, 0, x, H);
        for (int y = 18; y < H; y += 48) g.DrawLine(grid, 0, y, W, y);
        using var border = new Pen(Ink, 4);
        g.DrawRectangle(border, 2, 2, W - 4, H - 4);
    }

    private void DrawMenu(Graphics g)
    {
        DrawText(g, "DOODLE ROAD", 62, Ink, new RectangleF(0, 70, W, 85), ContentAlignment.MiddleCenter, true, "Segoe Print");
        DrawText(g, "МАСТЕРСКАЯ НА КОЛЁСАХ", 17, Color.FromArgb(110, 98, 82), new RectangleF(0, 147, W, 35), ContentAlignment.MiddleCenter, true);

        DrawShowcaseCar(g, 640, 260, 1.35f);
        using (var p = new Pen(Ink, 6) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawBezier(p, 325, 340, 460, 300, 755, 380, 945, 330);
            g.DrawLine(p, 895, 325, 945, 290);
        }
        DrawFlag(g, 945, 290, .75f);

        DrawText(g, "СЛОЖНОСТЬ", 16, Ink, new RectangleF(390, 386, 500, 30), ContentAlignment.MiddleCenter, true);
        string[] names = { "ЛЕГКО", "НОРМАЛЬНО", "СЛОЖНО" };
        Color[] cols = { Green, Blue, Red };
        for (int i = 0; i < 3; i++)
            DrawButton(g, new RectangleF(325 + i * 215, 420, 195, 52), names[i], cols[i], true, save.Difficulty == i);

        DrawButton(g, new RectangleF(460, 495, 360, 72), "ИГРАТЬ", Green);
        DrawButton(g, new RectangleF(350, 585, 270, 58), "УРОВНИ", Blue);
        DrawButton(g, new RectangleF(660, 585, 270, 58), "ГАРАЖ", Color.FromArgb(155, 86, 214));

        DrawIconButton(g, new RectangleF(25, 22, 54, 48), save.Sound ? "🔊" : "🔇", Paper);
        DrawStar(g, 1160, 46, 18, Gold);
        DrawText(g, save.TotalStars.ToString(), 22, Ink, new RectangleF(1180, 23, 72, 45), ContentAlignment.MiddleLeft, true);
        DrawText(g, "Сейв: рядом с .exe", 11, Color.FromArgb(125, 109, 90), new RectangleF(25, 675, 260, 25), ContentAlignment.MiddleLeft);
    }

    private void DrawLevels(Graphics g)
    {
        DrawText(g, "КАРТА УРОВНЕЙ", 39, Ink, new RectangleF(0, 35, W, 60), ContentAlignment.MiddleCenter, true, "Segoe Print");
        DrawIconButton(g, new RectangleF(25, 25, 70, 50), "←", Paper);
        int first = levelPage * 20 + 1;
        for (int i = 0; i < 20; i++)
        {
            int n = first + i;
            int col = i % 5, row = i / 5;
            var r = new RectangleF(135 + col * 210, 130 + row * 125, 165, 92);
            bool open = n <= save.UnlockedLevel && n <= MaxLevels;
            DrawButton(g, r, open ? n.ToString() : "🔒", open ? Color.FromArgb(250, 250, 244) : Color.FromArgb(207, 203, 193), open);
            if (open)
            {
                int stars = save.LevelStars.TryGetValue(n, out int s) ? s : 0;
                for (int z = 0; z < 3; z++) DrawStar(g, r.X + 52 + z * 30, r.Bottom - 17, 11, z < stars ? Gold : Color.FromArgb(205, 200, 185));
            }
        }
        DrawButton(g, new RectangleF(360, 640, 150, 48), "◀", Blue, levelPage > 0);
        DrawText(g, $"{levelPage + 1} / 10", 18, Ink, new RectangleF(515, 645, 250, 38), ContentAlignment.MiddleCenter, true);
        DrawButton(g, new RectangleF(770, 640, 150, 48), "▶", Blue, (levelPage + 1) * 20 < MaxLevels);
    }

    private void DrawGarage(Graphics g)
    {
        DrawText(g, "ГАРАЖ", 43, Ink, new RectangleF(0, 35, W, 65), ContentAlignment.MiddleCenter, true, "Segoe Print");
        DrawIconButton(g, new RectangleF(25, 25, 70, 50), "←", Paper);
        DrawShowcaseCar(g, 640, 205, 1.55f);
        DrawStar(g, 1080, 55, 18, Gold);
        DrawText(g, $"{save.TotalStars} звёзд", 20, Ink, new RectangleF(1100, 30, 150, 50), ContentAlignment.MiddleLeft, true);

        DrawText(g, "ЦВЕТ КУЗОВА", 18, Ink, new RectangleF(80, 335, 290, 34), ContentAlignment.MiddleLeft, true);
        int[] colorNeed = { 0, 3, 6, 10, 15, 22, 30, 40 };
        for (int i = 0; i < CarColors.Length; i++)
        {
            var r = new RectangleF(82 + i * 72, 380, 54, 54);
            bool open = save.TotalStars >= colorNeed[i];
            using var b = new SolidBrush(open ? CarColors[i] : Color.FromArgb(182, 179, 170));
            RoundRect(g, b, new Pen(save.BodyColor == i ? Ink : Color.FromArgb(120, Ink), save.BodyColor == i ? 5 : 2), r, 12);
            if (!open) DrawText(g, $"★{colorNeed[i]}", 10, Ink, r, ContentAlignment.MiddleCenter, true);
        }

        DrawText(g, "КУЗОВ", 18, Ink, new RectangleF(80, 466, 210, 32), ContentAlignment.MiddleLeft, true);
        string[] bodies = { "БАГГИ", "ФУРГОН", "БОЛИД" };
        int[] bodyNeed = { 0, 12, 24 };
        for (int i = 0; i < 3; i++)
            DrawButton(g, new RectangleF(80 + i * 190, 510, 165, 48), bodies[i] + (save.TotalStars < bodyNeed[i] ? $" ★{bodyNeed[i]}" : ""), Color.FromArgb(248, 247, 239), save.TotalStars >= bodyNeed[i], save.BodyStyle == i);

        DrawText(g, "КОЛЁСА", 18, Ink, new RectangleF(690, 466, 210, 32), ContentAlignment.MiddleLeft, true);
        string[] wheels = { "КЛАССИКА", "ВНЕДОРОЖ.", "НЕОН" };
        int[] wheelNeed = { 0, 8, 18 };
        for (int i = 0; i < 3; i++)
            DrawButton(g, new RectangleF(690 + i * 175, 510, 150, 48), wheels[i] + (save.TotalStars < wheelNeed[i] ? $" ★{wheelNeed[i]}" : ""), Color.FromArgb(248, 247, 239), save.TotalStars >= wheelNeed[i], save.WheelStyle == i);

        DrawText(g, "СЛЕД", 18, Ink, new RectangleF(80, 595, 170, 32), ContentAlignment.MiddleLeft, true);
        string[] trails = { "ПЫЛЬ", "КРАСНЫЙ", "СИНИЙ", "ЗОЛОТО" };
        int[] trailNeed = { 0, 10, 22, 35 };
        for (int i = 0; i < 4; i++)
            DrawButton(g, new RectangleF(240 + i * 215, 586, 190, 49), trails[i] + (save.TotalStars < trailNeed[i] ? $" ★{trailNeed[i]}" : ""), Color.FromArgb(248, 247, 239), save.TotalStars >= trailNeed[i], save.TrailStyle == i);

        DrawText(g, "Новые детали открываются за собранные звёзды", 13, Color.FromArgb(119, 102, 85), new RectangleF(0, 665, W, 25), ContentAlignment.MiddleCenter);
    }

    private void DrawGame(Graphics g)
    {
        if (level is null) return;
        var state = g.Save();
        if (shake > 0)
            g.TranslateTransform((float)(Random.Shared.NextDouble() - .5) * shake, (float)(Random.Shared.NextDouble() - .5) * shake);

        var viewport = new RectangleF(3, 64, W - 6, 540);
        g.SetClip(viewport);
        DrawWorld(g);
        g.Restore(state);

        using var top = new SolidBrush(Color.FromArgb(239, 233, 218));
        g.FillRectangle(top, 3, 3, W - 6, 61);
        using var line = new Pen(Ink, 3);
        g.DrawLine(line, 3, 64, W - 3, 64);
        DrawIconButton(g, new RectangleF(13, 10, 60, 45), "⌂", Paper);
        DrawText(g, $"УРОВЕНЬ {currentLevel}", 20, Ink, new RectangleF(90, 12, 175, 40), ContentAlignment.MiddleLeft, true);
        DrawText(g, DifficultySpec.For(save.Difficulty).Name, 14,
            save.Difficulty == 0 ? Green : save.Difficulty == 2 ? Red : Blue,
            new RectangleF(265, 13, 140, 38), ContentAlignment.MiddleLeft, true);

        DrawMiniMap(g);
        DrawIconButton(g, new RectangleF(1205, 10, 58, 45), save.Sound ? "🔊" : "🔇", Paper);

        if (phase == RunPhase.Build) DrawBuildPanel(g);
        else DrawDriveHud(g);

        if (phase is RunPhase.Paused or RunPhase.Won or RunPhase.Lost) DrawOverlay(g);

        if (phase == RunPhase.Build && currentLevel == 1 && !save.LevelStars.ContainsKey(1))
        {
            using var b = new SolidBrush(Color.FromArgb(224, 255, 250, 220));
            RoundRect(g, b, new Pen(Ink, 2), new RectangleF(250, 83, 780, 56), 14);
            DrawText(g, "Нарисуй мост через разрывы • добавь детали к машине • прокрути карту колёсиком", 14, Ink,
                new RectangleF(265, 94, 750, 34), ContentAlignment.MiddleCenter, true);
        }
    }

    private void DrawWorld(Graphics g)
    {
        if (level is null) return;
        float t = (float)clock.Elapsed.TotalSeconds;
        using (var hill = new SolidBrush(Color.FromArgb(35, 116, 169, 102)))
        {
            for (int i = -1; i < 8; i++)
            {
                float x = i * 260 - (camera * .18f % 260);
                g.FillEllipse(hill, x, 355, 420, 280);
            }
        }

        foreach (var td in trail)
        {
            float sx = td.Position.X - camera;
            float a = Math.Clamp(td.Life / .8f, 0, 1);
            using var b = new SolidBrush(Color.FromArgb((int)(150 * a), td.Color));
            float size = 9 + (1 - a) * 18;
            g.FillEllipse(b, sx - size / 2, td.Position.Y - size / 2, size, size);
        }

        using var earth = new Pen(Color.FromArgb(184, 165, 117), 18) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var terrainPen = new Pen(Ink, 6) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        foreach (var s in level.Terrain)
        {
            float ax = s.A.X - camera, bx = s.B.X - camera;
            if (Math.Max(ax, bx) < -40 || Math.Min(ax, bx) > W + 40) continue;
            g.DrawLine(earth, ax, s.A.Y + 8, bx, s.B.Y + 8);
            g.DrawLine(terrainPen, ax, s.A.Y, bx, s.B.Y);
        }

        foreach (var r in roads)
        {
            if (r.Points.Count < 2) continue;
            using var under = new Pen(r.Beam ? Color.FromArgb(160, 103, 55) : Color.White, r.Beam ? 17 : 13)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var pen = new Pen(Ink, r.Beam ? 5 : 6)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            var pts = r.Points.Select(p => new PointF(p.X - camera, p.Y)).ToArray();
            if (pts.Length > 1)
            {
                g.DrawLines(under, pts);
                g.DrawLines(pen, pts);
                if (r.Beam)
                {
                    using var grain = new Pen(Color.FromArgb(100, 72, 42), 2);
                    for (int i = 0; i < pts.Length - 1; i++)
                    {
                        float mx = (pts[i].X + pts[i + 1].X) / 2, my = (pts[i].Y + pts[i + 1].Y) / 2;
                        g.DrawEllipse(grain, mx - 2, my - 2, 4, 4);
                    }
                }
            }
        }

        if (beamStart.HasValue && phase == RunPhase.Build)
        {
            using var preview = new Pen(Color.FromArgb(180, Ink), 6) { DashStyle = DashStyle.Dash };
            g.DrawLine(preview, beamStart.Value.X - camera, beamStart.Value.Y, mouse.X, mouse.Y);
        }

        foreach (var p in level.Pickups)
            if (!p.Taken && p.X - camera > -40 && p.X - camera < W + 40)
                DrawCoin(g, p.X - camera, p.Y, t);

        foreach (var o in level.Obstacles)
            if (!o.Broken && o.X - camera > -160 && o.X - camera < W + 160)
                DrawObstacle(g, o, o.X - camera, t);

        DrawFlag(g, level.GoalX - camera, 430, 1f);
        DrawText(g, "ФИНИШ", 12, Ink, new RectangleF(level.GoalX - camera - 50, 384, 100, 28), ContentAlignment.MiddleCenter, true);

        DrawVehicle(g, car.X - camera, car.Y, car.Angle, 1);
    }

    private void DrawBuildPanel(Graphics g)
    {
        using var b = new SolidBrush(Color.FromArgb(246, 240, 226));
        g.FillRectangle(b, 3, 604, W - 6, 113);
        using var p = new Pen(Ink, 3);
        g.DrawLine(p, 3, 604, W - 3, 604);

        var spec = DifficultySpec.For(save.Difficulty);
        DrawText(g, $"ЧЕРНИЛА {Math.Max(0, (int)inkLeft)}/{(int)spec.Ink}", 13, Ink, new RectangleF(18, 615, 190, 24), ContentAlignment.MiddleLeft, true);
        DrawProgress(g, new RectangleF(18, 643, 185, 15), inkLeft / spec.Ink, Blue);
        DrawText(g, $"ДЕТАЛИ {partsLeft}/{spec.Parts}", 13, Ink, new RectangleF(18, 667, 190, 28), ContentAlignment.MiddleLeft, true);

        string[] labels = { "ДОРОГА", "БАЛКА", "КОЛЕСО", "ШАР", "РАКЕТА", "ЛАСТИК" };
        Color[] colors = { Color.White, Color.FromArgb(208, 145, 75), Color.FromArgb(90, 93, 102), Color.FromArgb(246, 103, 136), Color.FromArgb(243, 91, 42), Color.FromArgb(244, 182, 192) };
        for (int i = 0; i < labels.Length; i++)
            DrawButton(g, new RectangleF(225 + i * 130, 626, 116, 57), labels[i], colors[i], true, (int)tool == i);

        DrawButton(g, new RectangleF(1016, 620, 98, 67), "🎲", Color.FromArgb(249, 193, 67));
        DrawButton(g, new RectangleF(1125, 615, 135, 77), "ПОЕХАЛИ", Green);
        DrawText(g, "A/D или колесо — смотреть трассу", 10, Color.FromArgb(115, 98, 80), new RectangleF(865, 691, 390, 20), ContentAlignment.MiddleRight);
    }

    private void DrawDriveHud(Graphics g)
    {
        using var b = new SolidBrush(Color.FromArgb(239, 233, 218));
        g.FillRectangle(b, 3, 604, W - 6, 113);
        using var p = new Pen(Ink, 3);
        g.DrawLine(p, 3, 604, W - 3, 604);
        var spec = DifficultySpec.For(save.Difficulty);

        DrawText(g, "ТОПЛИВО", 13, Ink, new RectangleF(25, 619, 120, 25), ContentAlignment.MiddleLeft, true);
        DrawProgress(g, new RectangleF(25, 651, 250, 22), car.Fuel / spec.Fuel, car.Fuel < 25 ? Red : Green);
        DrawText(g, $"СКОРОСТЬ {(int)Math.Abs(car.Vx * 13)}", 17, Ink, new RectangleF(320, 626, 210, 35), ContentAlignment.MiddleLeft, true);
        DrawText(g, $"МОНЕТЫ {car.Coins}/{level?.Pickups.Count}", 17, Ink, new RectangleF(550, 626, 205, 35), ContentAlignment.MiddleLeft, true);
        DrawText(g, $"ВРЕМЯ {runTime:0.0}", 17, Ink, new RectangleF(770, 626, 170, 35), ContentAlignment.MiddleLeft, true);

        for (int i = 0; i < 3; i++)
            DrawHeart(g, 966 + i * 38, 644, 14, i >= car.Damage ? Red : Color.FromArgb(190, 186, 178));

        DrawButton(g, new RectangleF(1094, 620, 166, 65), "В МАСТЕРСКУ", Blue);
        DrawText(g, "→ газ • ← тормоз • ПРОБЕЛ ракеты • ESC пауза", 11, Color.FromArgb(110, 93, 76), new RectangleF(25, 684, 760, 23), ContentAlignment.MiddleLeft);
    }

    private void DrawMiniMap(Graphics g)
    {
        if (level is null) return;
        var r = new RectangleF(430, 18, 630, 27);
        using var bg = new SolidBrush(Color.FromArgb(215, 207, 190));
        RoundRect(g, bg, new Pen(Color.FromArgb(95, Ink), 2), r, 10);
        float progress = Math.Clamp(car.X / level.GoalX, 0, 1);
        using var fill = new SolidBrush(Green);
        if (progress > 0) RoundRect(g, fill, null, new RectangleF(r.X + 3, r.Y + 3, (r.Width - 6) * progress, r.Height - 6), 7);
        float px = r.X + r.Width * progress;
        using var carBrush = new SolidBrush(CarColors[save.BodyColor]);
        g.FillEllipse(carBrush, px - 7, r.Y + 5, 14, 14);
        using var pen = new Pen(Ink, 2);
        g.DrawEllipse(pen, px - 7, r.Y + 5, 14, 14);
    }

    private void DrawOverlay(Graphics g)
    {
        using var dim = new SolidBrush(Color.FromArgb(145, 20, 22, 28));
        g.FillRectangle(dim, 0, 0, W, H);
        var panel = new RectangleF(375, 145, 530, 430);
        using var paper = new SolidBrush(Paper);
        RoundRect(g, paper, new Pen(Ink, 5), panel, 28);

        if (phase == RunPhase.Paused)
        {
            DrawText(g, "ПАУЗА", 43, Ink, new RectangleF(0, 190, W, 65), ContentAlignment.MiddleCenter, true, "Segoe Print");
            DrawButton(g, new RectangleF(475, 300, 330, 66), "ПРОДОЛЖИТЬ", Green);
            DrawButton(g, new RectangleF(475, 390, 330, 60), "В МАСТЕРСКУ", Blue);
            DrawButton(g, new RectangleF(475, 475, 330, 55), "МЕНЮ", Color.FromArgb(180, 177, 169));
        }
        else if (phase == RunPhase.Won)
        {
            DrawText(g, "ФИНИШ!", 43, Green, new RectangleF(0, 182, W, 65), ContentAlignment.MiddleCenter, true, "Segoe Print");
            for (int i = 0; i < 3; i++) DrawStar(g, 560 + i * 80, 285, 31, i < resultStars ? Gold : Color.FromArgb(200, 195, 181));
            DrawText(g, $"Монеты: {car.Coins}/{level?.Pickups.Count}    Время: {runTime:0.0} сек.", 15, Ink,
                new RectangleF(430, 340, 420, 38), ContentAlignment.MiddleCenter, true);
            DrawButton(g, new RectangleF(465, 405, 350, 66), currentLevel < MaxLevels ? "СЛЕДУЮЩИЙ УРОВЕНЬ" : "ЕЩЁ РАЗ", Green);
            DrawButton(g, new RectangleF(465, 490, 165, 52), "ПОВТОР", Blue);
            DrawButton(g, new RectangleF(650, 490, 165, 52), "МЕНЮ", Color.FromArgb(185, 181, 172));
        }
        else
        {
            DrawText(g, "МАШИНКА СЛОМАЛАСЬ", 34, Red, new RectangleF(0, 190, W, 62), ContentAlignment.MiddleCenter, true, "Segoe Print");
            DrawText(g, car.Damage >= 3 ? "Три удара — и всё. Перестрой конструкцию." :
                car.Fuel <= 0 ? "Закончилось топливо. Сделай дорогу короче." :
                "Попробуй мост, дополнительные колёса или ракету.", 15, Ink,
                new RectangleF(420, 285, 440, 58), ContentAlignment.MiddleCenter, true);
            DrawButton(g, new RectangleF(465, 375, 350, 66), "В МАСТЕРСКУ", Blue);
            DrawButton(g, new RectangleF(465, 465, 165, 55), "ПОВТОР", Green);
            DrawButton(g, new RectangleF(650, 465, 165, 55), "МЕНЮ", Color.FromArgb(185, 181, 172));
        }
    }

    private void DrawObstacle(Graphics g, Obstacle o, float x, float t)
    {
        switch (o.Type)
        {
            case ObstacleType.Rock:
                using (var b = new SolidBrush(Color.FromArgb(122, 128, 130)))
                using (var p = new Pen(Ink, 4))
                {
                    PointF[] pts = { new(x - 37, o.BaseY), new(x - 30, o.BaseY - 42), new(x - 9, o.BaseY - 61), new(x + 25, o.BaseY - 50), new(x + 39, o.BaseY - 13), new(x + 30, o.BaseY) };
                    g.FillPolygon(b, pts); g.DrawPolygon(p, pts);
                    g.DrawLine(p, x - 12, o.BaseY - 49, x + 2, o.BaseY - 30);
                }
                break;
            case ObstacleType.Crate:
                using (var b = new SolidBrush(Color.FromArgb(197, 133, 62)))
                using (var p = new Pen(Ink, 4))
                {
                    g.FillRectangle(b, x - 35, o.BaseY - 67, 70, 67); g.DrawRectangle(p, x - 35, o.BaseY - 67, 70, 67);
                    g.DrawLine(p, x - 31, o.BaseY - 63, x + 31, o.BaseY - 4);
                    g.DrawLine(p, x + 31, o.BaseY - 63, x - 31, o.BaseY - 4);
                }
                break;
            case ObstacleType.Spikes:
                using (var b = new SolidBrush(Color.FromArgb(170, 174, 180)))
                using (var p = new Pen(Ink, 3))
                    for (int i = -2; i <= 2; i++)
                    {
                        PointF[] tri = { new(x + i * 18 - 10, o.BaseY), new(x + i * 18, o.BaseY - 34), new(x + i * 18 + 10, o.BaseY) };
                        g.FillPolygon(b, tri); g.DrawPolygon(p, tri);
                    }
                break;
            case ObstacleType.Saw:
                using (var p = new Pen(Ink, 5))
                using (var b = new SolidBrush(Color.FromArgb(202, 206, 209)))
                {
                    g.DrawLine(p, x, o.BaseY - 190, x, o.Y);
                    var pts = new List<PointF>();
                    for (int i = 0; i < 24; i++)
                    {
                        float a = i * MathF.PI * 2 / 24 + t * 2;
                        float r = i % 2 == 0 ? 39 : 28;
                        pts.Add(new PointF(x + MathF.Cos(a) * r, o.Y + MathF.Sin(a) * r));
                    }
                    g.FillPolygon(b, pts.ToArray()); g.DrawPolygon(p, pts.ToArray());
                    g.FillEllipse(Brushes.White, x - 9, o.Y - 9, 18, 18); g.DrawEllipse(p, x - 9, o.Y - 9, 18, 18);
                }
                break;
            case ObstacleType.Mud:
                using (var b = new SolidBrush(Color.FromArgb(128, 91, 53)))
                using (var p = new Pen(Ink, 3))
                {
                    g.FillEllipse(b, x - 82, o.BaseY - 16, 164, 28);
                    g.DrawEllipse(p, x - 82, o.BaseY - 16, 164, 28);
                    g.DrawArc(p, x - 28, o.BaseY - 10, 55, 12, 180, 180);
                }
                break;
            case ObstacleType.Wind:
                using (var p = new Pen(Color.FromArgb(73, 151, 214), 5) { StartCap = LineCap.Round, EndCap = LineCap.ArrowAnchor })
                    for (int i = 0; i < 3; i++)
                    {
                        float yy = o.BaseY - 45 - i * 43 + MathF.Sin(t * 2 + i) * 8;
                        g.DrawArc(p, x - 65, yy - 18, 115, 36, 180, 175);
                    }
                DrawText(g, "ВЕТЕР", 10, Blue, new RectangleF(x - 55, o.BaseY - 190, 110, 25), ContentAlignment.MiddleCenter, true);
                break;
            case ObstacleType.Wall:
                using (var b = new SolidBrush(Color.FromArgb(198, 86, 67)))
                using (var p = new Pen(Ink, 3))
                {
                    g.FillRectangle(b, x - 30, o.BaseY - 118, 60, 118);
                    g.DrawRectangle(p, x - 30, o.BaseY - 118, 60, 118);
                    for (int yy = 0; yy < 4; yy++)
                    {
                        g.DrawLine(p, x - 30, o.BaseY - yy * 29, x + 30, o.BaseY - yy * 29);
                        float xx = yy % 2 == 0 ? x : x - 15;
                        g.DrawLine(p, xx, o.BaseY - yy * 29, xx, o.BaseY - (yy + 1) * 29);
                    }
                }
                break;
        }
    }

    private void DrawVehicle(Graphics g, float x, float y, float angle, float size)
    {
        var state = g.Save();
        g.TranslateTransform(x, y);
        g.RotateTransform(angle * 180 / MathF.PI);
        g.ScaleTransform(size, size);

        foreach (var part in parts.Where(p => p.Type == PartType.Balloon))
        {
            V2 q = part.Offset;
            using var rope = new Pen(Ink, 2);
            g.DrawLine(rope, q.X, q.Y, q.X, q.Y - 55);
            using var bb = new SolidBrush(Color.FromArgb(244, 98, 135));
            g.FillEllipse(bb, q.X - 22, q.Y - 101, 44, 55);
            g.DrawEllipse(new Pen(Ink, 3), q.X - 22, q.Y - 101, 44, 55);
            g.DrawLine(rope, q.X - 5, q.Y - 47, q.X + 5, q.Y - 47);
        }

        Color body = CarColors[Math.Clamp(save.BodyColor, 0, CarColors.Length - 1)];
        using var bodyBrush = new SolidBrush(body);
        using var ink = new Pen(Ink, 5) { LineJoin = LineJoin.Round };

        if (save.BodyStyle == 1)
        {
            var pts = new[] { new PointF(-63, 20), new PointF(-62, -38), new PointF(38, -38), new PointF(62, -5), new PointF(62, 20) };
            g.FillPolygon(bodyBrush, pts); g.DrawPolygon(ink, pts);
            using var glass = new SolidBrush(Color.FromArgb(151, 218, 239));
            g.FillRectangle(glass, -40, -30, 36, 25); g.DrawRectangle(new Pen(Ink, 3), -40, -30, 36, 25);
            g.FillRectangle(glass, 5, -30, 27, 25); g.DrawRectangle(new Pen(Ink, 3), 5, -30, 27, 25);
        }
        else if (save.BodyStyle == 2)
        {
            var pts = new[] { new PointF(-70, 19), new PointF(-47, -12), new PointF(5, -25), new PointF(52, -8), new PointF(72, 19) };
            g.FillPolygon(bodyBrush, pts); g.DrawPolygon(ink, pts);
            using var glass = new SolidBrush(Color.FromArgb(151, 218, 239));
            g.FillPolygon(glass, new[] { new PointF(-28, -13), new PointF(3, -20), new PointF(26, -10) });
            g.DrawPolygon(new Pen(Ink, 3), new[] { new PointF(-28, -13), new PointF(3, -20), new PointF(26, -10) });
        }
        else
        {
            var pts = new[] { new PointF(-62, 20), new PointF(-62, -16), new PointF(-38, -35), new PointF(18, -35), new PointF(37, -14), new PointF(62, -8), new PointF(62, 20) };
            g.FillPolygon(bodyBrush, pts); g.DrawPolygon(ink, pts);
            using var glass = new SolidBrush(Color.FromArgb(151, 218, 239));
            var glassPts = new[] { new PointF(-32, -29), new PointF(12, -29), new PointF(28, -12), new PointF(-38, -12) };
            g.FillPolygon(glass, glassPts); g.DrawPolygon(new Pen(Ink, 3), glassPts);
        }

        DrawWheel(g, -38, 22, 18);
        DrawWheel(g, 38, 22, 18);
        foreach (var part in parts.Where(p => p.Type == PartType.Wheel))
            DrawWheel(g, part.Offset.X, part.Offset.Y, 17);

        foreach (var part in parts.Where(p => p.Type == PartType.Rocket))
        {
            V2 q = part.Offset;
            using var rb = new SolidBrush(Color.FromArgb(220, 222, 224));
            PointF[] rocket = { new(q.X - 30, q.Y - 13), new(q.X + 15, q.Y - 13), new(q.X + 30, q.Y), new(q.X + 15, q.Y + 13), new(q.X - 30, q.Y + 13) };
            g.FillPolygon(rb, rocket); g.DrawPolygon(new Pen(Ink, 3), rocket);
            using var fire = new SolidBrush(Color.FromArgb(250, 142, 35));
            if (phase == RunPhase.Drive && keys.Contains(Keys.Space))
                g.FillPolygon(fire, new[] { new PointF(q.X - 30, q.Y - 9), new PointF(q.X - 58 - Random.Shared.Next(0, 18), q.Y), new PointF(q.X - 30, q.Y + 9) });
        }

        if (car.CollisionCooldown > 0)
        {
            using var flash = new Pen(Color.FromArgb(230, 255, 75, 50), 5);
            g.DrawEllipse(flash, -82, -72, 164, 125);
        }

        g.Restore(state);
    }

    private void DrawShowcaseCar(Graphics g, float x, float y, float size)
    {
        var oldPhase = phase;
        DrawVehicle(g, x, y, -.03f, size);
        phase = oldPhase;
    }

    private void DrawWheel(Graphics g, float x, float y, float radius)
    {
        Color tire = save.WheelStyle == 2 ? Color.FromArgb(55, 226, 238) : Color.FromArgb(48, 50, 57);
        using var tb = new SolidBrush(tire);
        using var p = new Pen(Ink, 4);
        g.FillEllipse(tb, x - radius, y - radius, radius * 2, radius * 2); g.DrawEllipse(p, x - radius, y - radius, radius * 2, radius * 2);
        float hub = save.WheelStyle == 1 ? radius * .52f : radius * .38f;
        using var hb = new SolidBrush(save.WheelStyle == 2 ? Color.White : Color.FromArgb(221, 222, 216));
        g.FillEllipse(hb, x - hub, y - hub, hub * 2, hub * 2); g.DrawEllipse(new Pen(Ink, 2), x - hub, y - hub, hub * 2, hub * 2);
        if (save.WheelStyle == 1)
            for (int i = 0; i < 8; i++)
            {
                float a = i * MathF.PI / 4;
                g.DrawLine(new Pen(Ink, 2), x + MathF.Cos(a) * radius * .72f, y + MathF.Sin(a) * radius * .72f,
                    x + MathF.Cos(a) * radius, y + MathF.Sin(a) * radius);
            }
    }

    private void DrawCoin(Graphics g, float x, float y, float t)
    {
        float squish = .35f + Math.Abs(MathF.Sin(t * 3 + x)) * .65f;
        using var b = new SolidBrush(Gold);
        using var p = new Pen(Ink, 3);
        g.FillEllipse(b, x - 16 * squish, y - 18, 32 * squish, 36);
        g.DrawEllipse(p, x - 16 * squish, y - 18, 32 * squish, 36);
        if (squish > .55f) DrawText(g, "★", 12, Ink, new RectangleF(x - 13, y - 12, 26, 25), ContentAlignment.MiddleCenter, true);
    }

    private void DrawFlag(Graphics g, float x, float y, float size)
    {
        using var p = new Pen(Ink, 4 * size);
        g.DrawLine(p, x, y, x, y + 118 * size);
        int cells = 4;
        float cell = 17 * size;
        for (int yy = 0; yy < 3; yy++)
            for (int xx = 0; xx < cells; xx++)
            {
                using var b = new SolidBrush((xx + yy) % 2 == 0 ? Color.White : Ink);
                g.FillRectangle(b, x + xx * cell, y + yy * cell, cell, cell);
            }
        g.DrawRectangle(p, x, y, cells * cell, 3 * cell);
    }

    private void DrawHeart(Graphics g, float x, float y, float r, Color c)
    {
        using var b = new SolidBrush(c);
        using var path = new GraphicsPath();
        path.AddBezier(x, y + r * .35f, x - r * 1.3f, y - r * .7f, x - r * 1.5f, y + r * .8f, x, y + r * 1.7f);
        path.AddBezier(x, y + r * 1.7f, x + r * 1.5f, y + r * .8f, x + r * 1.3f, y - r * .7f, x, y + r * .35f);
        g.FillPath(b, path); g.DrawPath(new Pen(Ink, 2), path);
    }

    private void DrawStar(Graphics g, float x, float y, float radius, Color color)
    {
        var pts = new PointF[10];
        for (int i = 0; i < 10; i++)
        {
            float a = -MathF.PI / 2 + i * MathF.PI / 5;
            float r = i % 2 == 0 ? radius : radius * .46f;
            pts[i] = new PointF(x + MathF.Cos(a) * r, y + MathF.Sin(a) * r);
        }
        using var b = new SolidBrush(color);
        using var p = new Pen(Ink, Math.Max(1.5f, radius / 9));
        g.FillPolygon(b, pts); g.DrawPolygon(p, pts);
    }

    private void DrawProgress(Graphics g, RectangleF r, float value, Color color)
    {
        value = Math.Clamp(value, 0, 1);
        using var bg = new SolidBrush(Color.FromArgb(211, 205, 192));
        RoundRect(g, bg, new Pen(Ink, 2), r, r.Height / 2);
        if (value > .01f)
            using (var fill = new SolidBrush(color))
                RoundRect(g, fill, null, new RectangleF(r.X + 2, r.Y + 2, Math.Max(3, (r.Width - 4) * value), r.Height - 4), (r.Height - 4) / 2);
    }

    private void DrawIconButton(Graphics g, RectangleF r, string text, Color color)
        => DrawButton(g, r, text, color);

    private void DrawButton(Graphics g, RectangleF r, string text, Color color, bool enabled = true, bool selected = false)
    {
        Color c = enabled ? color : Color.FromArgb(196, 193, 184);
        using var shadow = new SolidBrush(Color.FromArgb(55, Ink));
        RoundRect(g, shadow, null, new RectangleF(r.X + 4, r.Y + 5, r.Width, r.Height), 10);
        using var fill = new SolidBrush(c);
        using var pen = new Pen(selected ? Gold : Ink, selected ? 5 : 2.5f);
        RoundRect(g, fill, pen, r, 10);
        DrawText(g, text, r.Height >= 65 ? 17 : 14, enabled ? Ink : Color.FromArgb(120, 116, 107), r, ContentAlignment.MiddleCenter, true);
    }

    private static void RoundRect(Graphics g, Brush? fill, Pen? pen, RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        using var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        if (fill is not null) g.FillPath(fill, path);
        if (pen is not null) g.DrawPath(pen, path);
    }

    private static void DrawText(Graphics g, string text, float size, Color color, RectangleF rect,
        ContentAlignment align, bool bold = false, string family = "Segoe UI")
    {
        using var font = new Font(family, size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var sf = new StringFormat
        {
            Alignment = align is ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter ? StringAlignment.Center :
                        align is ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight ? StringAlignment.Far : StringAlignment.Near,
            LineAlignment = align is ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight ? StringAlignment.Center :
                            align is ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight ? StringAlignment.Far : StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter
        };
        g.DrawString(text, font, brush, rect, sf);
    }

    private PointF VirtualPoint(Point client)
        => new((client.X - offsetX) / scale, (client.Y - offsetY) / scale);

    private void OnMouseDownGame(object? sender, MouseEventArgs e)
    {
        mouse = VirtualPoint(e.Location);
        if (e.Button is MouseButtons.Middle or MouseButtons.Right && screen == ScreenMode.Game && phase == RunPhase.Build)
        {
            draggingCamera = true;
            dragStartX = mouse.X;
            cameraStart = camera;
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        sound.Play("click");

        if (screen == ScreenMode.Menu) HandleMenuClick(mouse);
        else if (screen == ScreenMode.Levels) HandleLevelsClick(mouse);
        else if (screen == ScreenMode.Garage) HandleGarageClick(mouse);
        else if (screen == ScreenMode.Game) HandleGameClick(mouse);
    }

    private void OnMouseMoveGame(object? sender, MouseEventArgs e)
    {
        mouse = VirtualPoint(e.Location);
        if (draggingCamera && level is not null)
        {
            camera = Math.Clamp(cameraStart + dragStartX - mouse.X, 0, Math.Max(0, level.Width - W));
            return;
        }
        if (drawingStroke is not null && phase == RunPhase.Build && e.Button == MouseButtons.Left)
        {
            V2 world = new(mouse.X + camera, mouse.Y);
            V2 last = drawingStroke.Points[^1];
            float d = V2.Distance(last, world);
            if (d >= 7 && inkLeft >= d)
            {
                drawingStroke.Points.Add(world);
                inkLeft -= d;
                if (drawingStroke.Points.Count % 9 == 0) sound.Play("draw");
            }
        }
    }

    private void OnMouseUpGame(object? sender, MouseEventArgs e)
    {
        mouse = VirtualPoint(e.Location);
        draggingCamera = false;
        if (e.Button != MouseButtons.Left || phase != RunPhase.Build) return;

        if (drawingStroke is not null)
        {
            if (drawingStroke.Points.Count < 2) roads.Remove(drawingStroke);
            drawingStroke = null;
        }
        if (beamStart.HasValue)
        {
            V2 end = new(mouse.X + camera, mouse.Y);
            float len = V2.Distance(beamStart.Value, end);
            if (len >= 16 && len <= inkLeft + 1)
            {
                var r = new RoadStroke { Beam = true };
                r.Points.Add(beamStart.Value); r.Points.Add(end);
                roads.Add(r);
                inkLeft -= len;
                sound.Play("draw");
            }
            else if (len > inkLeft) ShowToast("Не хватает чернил");
            beamStart = null;
        }
    }

    private void OnMouseWheelGame(object? sender, MouseEventArgs e)
    {
        if (screen == ScreenMode.Game && phase == RunPhase.Build && level is not null)
            camera = Math.Clamp(camera - e.Delta * 1.4f, 0, Math.Max(0, level.Width - W));
    }

    private void HandleMenuClick(PointF p)
    {
        if (Hit(p, 25, 22, 54, 48)) ToggleSound();
        else if (Hit(p, 325, 420, 195, 52)) SetDifficulty(0);
        else if (Hit(p, 540, 420, 195, 52)) SetDifficulty(1);
        else if (Hit(p, 755, 420, 195, 52)) SetDifficulty(2);
        else if (Hit(p, 460, 495, 360, 72)) NewLevel(save.UnlockedLevel);
        else if (Hit(p, 350, 585, 270, 58)) { screen = ScreenMode.Levels; levelPage = (save.UnlockedLevel - 1) / 20; }
        else if (Hit(p, 660, 585, 270, 58)) screen = ScreenMode.Garage;
    }

    private void SetDifficulty(int value)
    {
        save.Difficulty = value;
        SaveGame();
    }

    private void HandleLevelsClick(PointF p)
    {
        if (Hit(p, 25, 25, 70, 50)) { screen = ScreenMode.Menu; return; }
        int first = levelPage * 20 + 1;
        for (int i = 0; i < 20; i++)
        {
            int col = i % 5, row = i / 5;
            if (Hit(p, 135 + col * 210, 130 + row * 125, 165, 92))
            {
                int n = first + i;
                if (n <= save.UnlockedLevel && n <= MaxLevels) NewLevel(n);
                else ShowToast("Сначала пройди предыдущие уровни");
                return;
            }
        }
        if (Hit(p, 360, 640, 150, 48) && levelPage > 0) levelPage--;
        if (Hit(p, 770, 640, 150, 48) && (levelPage + 1) * 20 < MaxLevels) levelPage++;
    }

    private void HandleGarageClick(PointF p)
    {
        if (Hit(p, 25, 25, 70, 50)) { screen = ScreenMode.Menu; SaveGame(); return; }
        int[] colorNeed = { 0, 3, 6, 10, 15, 22, 30, 40 };
        for (int i = 0; i < CarColors.Length; i++)
            if (Hit(p, 82 + i * 72, 380, 54, 54)) { SelectUnlocked(colorNeed[i], () => save.BodyColor = i); return; }
        int[] bodyNeed = { 0, 12, 24 };
        for (int i = 0; i < 3; i++)
            if (Hit(p, 80 + i * 190, 510, 165, 48)) { SelectUnlocked(bodyNeed[i], () => save.BodyStyle = i); return; }
        int[] wheelNeed = { 0, 8, 18 };
        for (int i = 0; i < 3; i++)
            if (Hit(p, 690 + i * 175, 510, 150, 48)) { SelectUnlocked(wheelNeed[i], () => save.WheelStyle = i); return; }
        int[] trailNeed = { 0, 10, 22, 35 };
        for (int i = 0; i < 4; i++)
            if (Hit(p, 240 + i * 215, 586, 190, 49)) { SelectUnlocked(trailNeed[i], () => save.TrailStyle = i); return; }
    }

    private void SelectUnlocked(int needed, Action select)
    {
        if (save.TotalStars >= needed)
        {
            select(); SaveGame(); sound.Play("click");
        }
        else ShowToast($"Нужно звёзд: {needed}");
    }

    private void HandleGameClick(PointF p)
    {
        if (Hit(p, 13, 10, 60, 45)) { screen = ScreenMode.Menu; SaveGame(); return; }
        if (Hit(p, 1205, 10, 58, 45)) { ToggleSound(); return; }

        if (phase == RunPhase.Build)
        {
            for (int i = 0; i < 6; i++)
                if (Hit(p, 225 + i * 130, 626, 116, 57)) { tool = (BuildTool)i; return; }

            if (Hit(p, 1016, 620, 98, 67))
            {
                reroll++;
                BuildLevel();
                ShowToast("Создана новая случайная трасса");
                return;
            }
            if (Hit(p, 1125, 615, 135, 77)) { StartDrive(); return; }
            if (p.Y < 72 || p.Y > 600) return;

            V2 world = new(p.X + camera, p.Y);
            if (tool == BuildTool.Road)
            {
                if (inkLeft < 10) { ShowToast("Чернила закончились"); return; }
                drawingStroke = new RoadStroke();
                drawingStroke.Points.Add(world);
                roads.Add(drawingStroke);
            }
            else if (tool == BuildTool.Beam)
            {
                if (inkLeft < 18) { ShowToast("Чернила закончились"); return; }
                beamStart = world;
            }
            else if (tool == BuildTool.Eraser)
            {
                EraseAt(world);
            }
            else
            {
                PlacePart(world);
            }
        }
        else if (phase == RunPhase.Drive)
        {
            if (Hit(p, 1094, 620, 166, 65)) BackToBuild();
        }
        else if (phase == RunPhase.Paused)
        {
            if (Hit(p, 475, 300, 330, 66)) phase = RunPhase.Drive;
            else if (Hit(p, 475, 390, 330, 60)) BackToBuild();
            else if (Hit(p, 475, 475, 330, 55)) { screen = ScreenMode.Menu; SaveGame(); }
        }
        else if (phase == RunPhase.Won)
        {
            if (Hit(p, 465, 405, 350, 66)) NewLevel(currentLevel < MaxLevels ? currentLevel + 1 : currentLevel);
            else if (Hit(p, 465, 490, 165, 52)) { foreach (var x in level!.Pickups) x.Taken = false; StartDrive(); }
            else if (Hit(p, 650, 490, 165, 52)) screen = ScreenMode.Menu;
        }
        else if (phase == RunPhase.Lost)
        {
            if (Hit(p, 465, 375, 350, 66)) BackToBuild();
            else if (Hit(p, 465, 465, 165, 55)) { foreach (var x in level!.Pickups) x.Taken = false; StartDrive(); }
            else if (Hit(p, 650, 465, 165, 55)) screen = ScreenMode.Menu;
        }
    }

    private void PlacePart(V2 world)
    {
        if (partsLeft <= 0) { ShowToast("Лимит деталей исчерпан"); return; }
        V2 anchor = new(125, 390);
        if (Math.Abs(world.X - anchor.X) > 210 || Math.Abs(world.Y - anchor.Y) > 155)
        {
            ShowToast("Детали крепятся рядом с машинкой");
            return;
        }
        PartType type = tool switch { BuildTool.Wheel => PartType.Wheel, BuildTool.Balloon => PartType.Balloon, _ => PartType.Rocket };
        V2 offset = world - anchor;
        if (type == PartType.Wheel) offset = new V2(offset.X, Math.Max(20, offset.Y));
        if (type == PartType.Balloon) offset = new V2(offset.X, Math.Min(-20, offset.Y));
        if (type == PartType.Rocket) offset = new V2(Math.Min(-20, offset.X), offset.Y);
        parts.Add(new VehiclePart { Type = type, Offset = offset });
        partsLeft--;
        ShowToast(type switch { PartType.Wheel => "Колесо установлено", PartType.Balloon => "Шар установлен", _ => "Ракета установлена" });
    }

    private void EraseAt(V2 world)
    {
        VehiclePart? nearestPart = parts.OrderBy(x => V2.Distance(new V2(125, 390) + x.Offset, world)).FirstOrDefault();
        if (nearestPart is not null && V2.Distance(new V2(125, 390) + nearestPart.Offset, world) < 38)
        {
            parts.Remove(nearestPart); partsLeft++; return;
        }

        RoadStroke? hit = null;
        float best = 32;
        foreach (var r in roads)
            foreach (var pt in r.Points)
            {
                float d = V2.Distance(pt, world);
                if (d < best) { best = d; hit = r; }
            }
        if (hit is not null)
        {
            inkLeft += hit.Length;
            roads.Remove(hit);
        }
    }

    private void ToggleSound()
    {
        save.Sound = !save.Sound;
        sound.Enabled = save.Sound;
        if (save.Sound) sound.Play("click");
        SaveGame();
    }

    private void OnKeyDownGame(object? sender, KeyEventArgs e)
    {
        keys.Add(e.KeyCode);
        if (e.KeyCode == Keys.F11)
        {
            FormBorderStyle = FormBorderStyle.None;
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        }
        if (e.KeyCode == Keys.M) ToggleSound();
        if (screen != ScreenMode.Game) return;
        if (e.KeyCode == Keys.Escape)
        {
            if (phase == RunPhase.Drive) phase = RunPhase.Paused;
            else if (phase == RunPhase.Paused) phase = RunPhase.Drive;
            else { screen = ScreenMode.Menu; SaveGame(); }
        }
        if (e.KeyCode == Keys.R && phase is RunPhase.Drive or RunPhase.Lost) BackToBuild();
    }

    private static bool Hit(PointF p, float x, float y, float w, float h)
        => p.X >= x && p.X <= x + w && p.Y >= y && p.Y <= y + h;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            sound.Dispose();
        }
        base.Dispose(disposing);
    }
}
