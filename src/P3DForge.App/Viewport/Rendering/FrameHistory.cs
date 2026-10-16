using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace P3DForge.App.Viewport.Rendering;

/// <summary>Ring buffer of recent frame timings + the counters of the last frame (stats overlay, Render panel).</summary>
public sealed class FrameHistory
{
    public const int Capacity = 120;
    readonly double[] cpu = new double[Capacity], interval = new double[Capacity], gpu = new double[Capacity];
    int head, count;

    public int DrawCalls { get; private set; }
    public long Triangles { get; private set; }
    public int Instances { get; private set; }
    public double LastCpuMs { get; private set; }
    public double LastGpuMs { get; private set; } = double.NaN;
    public int Frames { get; private set; }

    public void Record(double cpuMs, double intervalMs, double gpuMs, FrameStats stats)
    {
        cpu[head] = cpuMs;
        interval[head] = intervalMs;
        gpu[head] = gpuMs;
        head = (head + 1) % Capacity;
        count = Math.Min(count + 1, Capacity);
        DrawCalls = stats.DrawCalls;
        Triangles = stats.Triangles;
        Instances = stats.Instances;
        LastCpuMs = cpuMs;
        LastGpuMs = gpuMs;
        Frames++;
        lastFrame = Stopwatch.GetTimestamp();
    }

    long lastFrame;

    /// <summary>True when no frame was drawn for a while: the viewport redraws only when something changes.</summary>
    public bool IsIdle => lastFrame == 0 || Stopwatch.GetElapsedTime(lastFrame).TotalMilliseconds > 600;

    /// <summary>Values oldest → newest: 0 = CPU ms, 1 = interval between frames, 2 = GPU ms.</summary>
    public IEnumerable<double> Series(int which)
    {
        var src = which switch { 1 => interval, 2 => gpu, _ => cpu };
        for (int i = 0; i < count; i++) yield return src[(head - count + i + Capacity) % Capacity];
    }

    /// <summary>Frames per second over the recent continuous frames (gaps &gt; 250 ms = idle, ignored).</summary>
    public double Fps
    {
        get
        {
            double sum = 0;
            int n = 0;
            for (int i = 0; i < Math.Min(count, 60); i++)
            {
                double v = interval[(head - 1 - i + Capacity) % Capacity];
                if (!(v > 0) || v > 250) break;
                sum += v;
                n++;
            }
            return n == 0 ? double.NaN : 1000.0 * n / sum;
        }
    }

    /// <summary>Average CPU ms over the last <paramref name="frames"/> frames.</summary>
    public double AverageCpu(int frames = 30)
    {
        double sum = 0;
        int n = Math.Min(count, frames);
        for (int i = 0; i < n; i++) sum += cpu[(head - 1 - i + Capacity) % Capacity];
        return n == 0 ? 0 : sum / n;
    }

    public string Format()
    {
        static string F(double v) => double.IsFinite(v) ? v.ToString("F1", CultureInfo.InvariantCulture) : "–";
        var sb = new StringBuilder();
        double fps = IsIdle ? double.NaN : Fps;
        sb.Append("FPS ").Append(double.IsFinite(fps) ? fps.ToString("F0", CultureInfo.InvariantCulture) : "idle (redraws on change)");
        sb.Append("   frame ").Append(F(LastCpuMs)).Append(" ms\n");
        sb.Append("CPU ").Append(F(AverageCpu())).Append(" ms   GPU ").Append(F(LastGpuMs)).Append(" ms\n");
        sb.Append("draws ").Append(DrawCalls).Append("   tris ").Append(Triangles.ToString("N0", CultureInfo.InvariantCulture));
        if (Instances > 0) sb.Append("   proxies ").Append(Instances);
        return sb.ToString();
    }
}
