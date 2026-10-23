using System.Diagnostics;

namespace WinTidy.Core;

public record ProcSample(int Pid, string Name, string? Path, long Memory, double Cpu);

public class ProcRow : ObservableObject
{
    public required string Key { get; init; }
    public required string Name { get; init; }

    string? _path;
    public string? Path { get => _path; set => Set(ref _path, value); }

    int _count;
    public int Count { get => _count; set => Set(ref _count, value); }

    double _cpu;
    public double Cpu { get => _cpu; set { if (Set(ref _cpu, Math.Round(value, 1))) Raise(nameof(CpuText)); } }
    public string CpuText => Cpu < 0.1 ? "0%" : $"{Cpu:0.#}%";

    long _memory;
    public long Memory { get => _memory; set { if (Set(ref _memory, value)) Raise(nameof(MemoryText)); } }
    public string MemoryText => Fmt.Bytes(Memory);

    public List<int> Pids { get; set; } = [];
}

/// <summary>Samples all processes; CPU% is computed from the difference to the previous sample.</summary>
public class ProcessSampler
{
    readonly Dictionary<int, long> _prevCpu = new();
    DateTime _prevAt = DateTime.MinValue;

    public List<ProcSample> Sample()
    {
        var now = DateTime.UtcNow;
        double elapsed100ns = _prevAt == DateTime.MinValue ? 0 : (now - _prevAt).TotalMilliseconds * 10_000;
        var current = new Dictionary<int, long>();
        var list = new List<ProcSample>();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == 0) continue;
                long mem = 0;
                try { mem = p.WorkingSet64; } catch { }
                string? path = null;
                double cpu = 0;
                var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (h != IntPtr.Zero)
                {
                    try
                    {
                        path = Native.GetProcessPath(h);
                        if (Native.GetProcessTimes(h, out _, out _, out var k, out var u))
                        {
                            long total = k + u;
                            current[p.Id] = total;
                            if (elapsed100ns > 0 && _prevCpu.TryGetValue(p.Id, out var prev))
                                cpu = Math.Max(0, (total - prev) / elapsed100ns * 100 / Environment.ProcessorCount);
                        }
                    }
                    finally { Native.CloseHandle(h); }
                }
                list.Add(new ProcSample(p.Id, p.ProcessName, path, mem, cpu));
            }
        }

        _prevCpu.Clear();
        foreach (var kv in current) _prevCpu[kv.Key] = kv.Value;
        _prevAt = now;
        return list;
    }

    static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost",
        "fontdrvhost", "dwm", "Memory Compression", "LsaIso", "MsMpEng", "SecurityHealthService",
    };

    public static bool IsCritical(string name) => Critical.Contains(name);
}
