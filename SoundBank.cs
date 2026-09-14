using System.Media;

namespace DoodleRoadWorkshop;

internal sealed class SoundBank : IDisposable
{
    private readonly Dictionary<string, (MemoryStream Stream, SoundPlayer Player)> sounds = new();
    public bool Enabled { get; set; } = true;

    public SoundBank()
    {
        Add("click", (i, t) => Math.Sin(t * 880 * Math.PI * 2) * Math.Exp(-t * 18), .07);
        Add("draw", (i, t) => Math.Sin(t * (260 + i % 60) * Math.PI * 2) * Math.Exp(-t * 11), .09);
        Add("start", (i, t) => Math.Sin(t * (180 + t * 520) * Math.PI * 2) * Math.Exp(-t * 3), .34);
        Add("coin", (i, t) => (Math.Sin(t * 900 * Math.PI * 2) + Math.Sin(t * 1350 * Math.PI * 2) * .45) * Math.Exp(-t * 8), .18);
        Add("hit", (i, t) => ((Random.Shared.NextDouble() * 2 - 1) * .7 + Math.Sin(t * 90 * Math.PI * 2) * .3) * Math.Exp(-t * 9), .28);
        Add("win", (i, t) =>
        {
            int note = Math.Min(3, (int)(t / .18));
            double[] hz = { 523.25, 659.25, 783.99, 1046.5 };
            return Math.Sin(t * hz[note] * Math.PI * 2) * Math.Exp(-(t % .18) * 4);
        }, .78);
        Add("fail", (i, t) => Math.Sin(t * (270 - t * 150) * Math.PI * 2) * Math.Exp(-t * 2.5), .55);
        Add("rocket", (i, t) => ((Random.Shared.NextDouble() * 2 - 1) * .5 + Math.Sin(t * 75 * Math.PI * 2) * .35) * Math.Exp(-t * 4), .16);
    }

    private void Add(string name, Func<int, double, double> wave, double seconds)
    {
        const int sampleRate = 22050;
        int count = (int)(sampleRate * seconds);
        var ms = new MemoryStream(44 + count * 2);
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.ASCII, true))
        {
            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + count * 2);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(sampleRate);
            bw.Write(sampleRate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(count * 2);
            for (int i = 0; i < count; i++)
            {
                double t = i / (double)sampleRate;
                double fadeIn = Math.Min(1, t * 80);
                short s = (short)(Math.Clamp(wave(i, t) * fadeIn, -1, 1) * 12500);
                bw.Write(s);
            }
        }
        ms.Position = 0;
        var player = new SoundPlayer(ms);
        player.Load();
        sounds[name] = (ms, player);
    }

    public void Play(string name)
    {
        if (!Enabled || !sounds.TryGetValue(name, out var sound)) return;
        try { sound.Player.Play(); } catch { }
    }

    public void Dispose()
    {
        foreach (var s in sounds.Values)
        {
            s.Player.Dispose();
            s.Stream.Dispose();
        }
        sounds.Clear();
    }
}
