using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace OneShader.Bench;

/// <summary>Records the machine's 1-minute load average every few seconds while a measurement runs.</summary>
public sealed class LoadSampler : IDisposable
{
    private readonly Timer _timer;
    private readonly List<double> _samples = [];
    private readonly object _gate = new();

    /// <summary>Takes a first sample now and then one every <paramref name="seconds"/>.</summary>
    public LoadSampler(int seconds = 5)
    {
        _timer = new Timer(_ => Sample(), null, 0, seconds * 1000);
    }

    /// <summary>The 1-minute load averages read so far, oldest first (empty where /proc/loadavg does not exist).</summary>
    public IReadOnlyList<double> Loads
    {
        get
        {
            lock (_gate)
            {
                return _samples.ToArray();
            }
        }
    }

    /// <summary>Takes a sample now.</summary>
    public void Sample()
    {
        try
        {
            string[] f = File.ReadAllText("/proc/loadavg").Split(' ');
            double load = double.Parse(f[0], CultureInfo.InvariantCulture);
            lock (_gate)
            {
                _samples.Add(load);
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            // No readable /proc/loadavg: no samples.
        }
    }

    /// <summary>Stops sampling.</summary>
    public void Dispose() => _timer.Dispose();
}
