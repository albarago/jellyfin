using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// One ffmpeg process of the capacity test and its live statistics.
/// </summary>
internal sealed partial class TranscodeTestStream
{
    /// <summary>
    /// Seconds over which a stream's frame rate is measured.
    /// </summary>
    internal const double WindowSeconds = 3;

    private const int ErrorLineCount = 4;
    private readonly object _lock = new();
    private readonly Queue<(double Time, long Frame)> _points = new();
    private readonly Queue<string> _errors = new();
    private readonly List<double> _samples = [];
    private readonly HashSet<string> _counted = new(StringComparer.Ordinal);
    private long _outputBytes;
    private int _outputSegments;
    private bool _disposed;
    private int _exitCode;

    public TranscodeTestStream(int id, Process process, string folder, string filePrefix, float sourceFps, double startedAt, int segmentLength)
    {
        Id = id;
        Process = process;
        Folder = folder;
        FilePrefix = filePrefix;
        SourceFps = sourceFps;
        StartedAt = startedAt;
        SegmentLength = segmentLength;
    }

    public int Id { get; }

    public Process Process { get; }

    public string Folder { get; }

    public string FilePrefix { get; }

    public float SourceFps { get; }

    public double StartedAt { get; }

    public int SegmentLength { get; }

    public bool Stopping { get; set; }

    public bool Handled { get; set; }

    public bool HasExited => _disposed || Process.HasExited;

    /// <summary>
    /// Gets or sets the frame rate when the test ended.
    /// </summary>
    public double? FinalFps { get; set; }

    public int ExitCode => _disposed ? _exitCode : Process.ExitCode;

    /// <summary>
    /// Gets ffmpeg's last error lines, whole and in order, joined with " | ". Empty when it printed none.
    /// </summary>
    public string LastError => string.Join(" | ", ErrorLines);

    /// <summary>
    /// Gets ffmpeg's last few error lines, whole and in order, with a line identical to the one before it left out.
    /// </summary>
    public IReadOnlyList<string> ErrorLines
    {
        get
        {
            lock (_lock)
            {
                return _errors.ToArray();
            }
        }
    }

    public double? OutputMbps
    {
        get
        {
            lock (_lock)
            {
                return _outputSegments > 0 ? Math.Round(_outputBytes * 8.0 / (_outputSegments * SegmentLength) / 1e6, 2) : null;
            }
        }
    }

    public double? MedianFps
    {
        get
        {
            lock (_lock)
            {
                if (_samples.Count == 0)
                {
                    return null;
                }

                var sorted = _samples.Order().ToList();
                return Math.Round(sorted[sorted.Count / 2], 1);
            }
        }
    }

    /// <summary>
    /// Reads ffmpeg's stats lines (frame=…), which it ends with carriage returns.
    /// </summary>
    /// <param name="clock">The test's clock.</param>
    /// <returns>A task that completes when ffmpeg closes its error output.</returns>
    public async Task ReadProgressAsync(Stopwatch clock)
    {
        var reader = Process.StandardError;
        var line = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c is '\r' or '\n')
                {
                    OnLine(line.ToString(), clock.Elapsed.TotalSeconds);
                    line.Clear();
                }
                else
                {
                    line.Append(c);
                }
            }
        }
    }

    /// <summary>
    /// Releases the process, keeping its exit code.
    /// </summary>
    public void DisposeProcess()
    {
        if (_disposed)
        {
            return;
        }

        _exitCode = Process.HasExited ? Process.ExitCode : -1;
        _disposed = true;
        Process.Dispose();
    }

    public double? Fps(double now)
    {
        lock (_lock)
        {
            if (_points.Count < 2)
            {
                return null;
            }

            var (t0, f0) = _points.First();
            var (t1, f1) = _points.Last();
            if (t1 - t0 < WindowSeconds / 2 || now - t1 > Math.Max(WindowSeconds, 2))
            {
                return null;
            }

            return (f1 - f0) / (t1 - t0);
        }
    }

    public void Sample(double now)
    {
        var fps = Fps(now);
        lock (_lock)
        {
            if (fps.HasValue)
            {
                _samples.Add(fps.Value);
            }
        }
    }

    /// <summary>
    /// Takes over the output and frame rate samples of the process this one replaces, when a stream is started again
    /// at the end of the file, so the stream's output bitrate and median frame rate cover its whole run.
    /// </summary>
    /// <param name="previous">The process that reached the end of the file.</param>
    public void CarryOver(TranscodeTestStream previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        long bytes;
        int segments;
        double[] samples;
        lock (previous._lock)
        {
            bytes = previous._outputBytes;
            segments = previous._outputSegments;
            samples = previous._samples.ToArray();
        }

        lock (_lock)
        {
            _outputBytes += bytes;
            _outputSegments += segments;
            _samples.InsertRange(0, samples);
        }
    }

    /// <summary>
    /// Counts finished segments for the output bitrate and deletes all but the newest few, so a long test does not fill the disk.
    /// </summary>
    /// <param name="exited">Whether ffmpeg has exited, so the newest segment is finished too.</param>
    public void CollectSegments(bool exited = false)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(Folder, FilePrefix + "*")
                .Where(f => f.EndsWith(".ts", StringComparison.Ordinal) || f.EndsWith(".mp4", StringComparison.Ordinal))
                .Select(f => (Path: f, Number: SegmentNumber(f)))
                .Where(f => f.Number >= 0)
                .OrderBy(f => f.Number)
                .Select(f => f.Path)
                .ToArray();
        }
        catch (IOException)
        {
            return;
        }

        // The newest segment may still be being written while ffmpeg runs.
        var finished = exited || files.Length == 0 ? files : files[..^1];
        lock (_lock)
        {
            foreach (var file in finished.Where(f => !_counted.Contains(f)))
            {
                try
                {
                    _outputBytes += new FileInfo(file).Length;
                    _outputSegments++;
                    _counted.Add(file);
                }
                catch (IOException)
                {
                }
            }
        }

        foreach (var file in finished.SkipLast(3))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    // Segments are named <prefix><number>.ts; the fMP4 header is <prefix>-1.mp4.
    private int SegmentNumber(string path)
        => int.TryParse(Path.GetFileNameWithoutExtension(path).AsSpan(FilePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : -1;

    private void OnLine(string line, double now)
    {
        var match = FrameRegex().Match(line);
        if (match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame))
        {
            lock (_lock)
            {
                if (_points.Count > 0 && frame < _points.Last().Frame)
                {
                    _points.Clear();
                }

                _points.Enqueue((now, frame));
                while (_points.Count > 2 && now - _points.ElementAt(1).Time >= WindowSeconds)
                {
                    _points.Dequeue();
                }
            }

            return;
        }

        if (line.Contains("rror", StringComparison.Ordinal) || line.Contains("ailed", StringComparison.Ordinal) || line.Contains("nvalid", StringComparison.Ordinal))
        {
            var error = line.Trim();
            lock (_lock)
            {
                // Keep whole lines, so the shown error never starts mid-line, and leave out a repeat of the line before.
                if (_errors.Count == 0 || !string.Equals(_errors.Last(), error, StringComparison.Ordinal))
                {
                    _errors.Enqueue(error);
                    while (_errors.Count > ErrorLineCount)
                    {
                        _errors.Dequeue();
                    }
                }
            }
        }
    }

    [GeneratedRegex(@"^frame=\s*(\d+)")]
    private static partial Regex FrameRegex();
}
