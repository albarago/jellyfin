using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Models.TranscodeTestDtos;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// Runs the dashboard's transcoding tests: a compatibility check of hardware decoding, encoding and tone mapping with
/// generated clips, and a stress test that adds streams of the playback HLS command until one falls behind real time.
/// </summary>
public sealed class TranscodeTestManager : IDisposable
{
    /// <summary>
    /// Each stream must keep this fraction of the source frame rate: real time. A stream below it falls further behind
    /// the longer it plays, until the client runs out of buffer. Short dips are allowed by <see cref="FailSeconds"/>.
    /// </summary>
    public const double FpsRatio = 1.0;

    /// <summary>
    /// Seconds every stream must keep up before another is added.
    /// </summary>
    public const double RampSeconds = 12;

    /// <summary>
    /// Seconds below the threshold that end the stress test.
    /// </summary>
    public const double FailSeconds = 5;

    /// <summary>
    /// Seconds a stress test stream may take to report its first frame.
    /// </summary>
    public const double StartTimeoutSeconds = 30;

    /// <summary>
    /// Seconds a source should last at least. Each stream restarts at the end of the file, which briefly disturbs the
    /// measurement.
    /// </summary>
    public const double ShortSourceSeconds = 60;

    /// <summary>
    /// Arguments put before the stress test's playback command. The level tag tells errors from warnings, and without
    /// the banner a stalled stream's last lines are ffmpeg's own output, not its build configuration.
    /// </summary>
    internal const string StressLogArguments = "-hide_banner -loglevel +level ";

    private const string FolderName = "transcode-test";
    private const int CheckFrames = 96;
    private const int MaxLogLines = 500;

    private static readonly string[] _allDecodeCodecs = ["h264", "hevc", "mpeg2video", "mpeg4", "vc1", "vp8", "vp9", "av1"];

    private static readonly ClipSpec[] _clips =
    [
        new("h264", 8, false, "h264.mkv", "libx264", "yuv420p", string.Empty),
        new("hevc", 8, false, "hevc8.mkv", "libx265", "yuv420p", "-x265-params log-level=none"),
        new("hevc", 10, false, "hevc10.mkv", "libx265", "yuv420p10le", "-x265-params log-level=none"),
        new("vp8", 8, false, "vp8.webm", "libvpx", "yuv420p", "-deadline realtime -cpu-used 8 -b:v 4M"),
        new("vp9", 8, false, "vp9.webm", "libvpx-vp9", "yuv420p", "-deadline realtime -cpu-used 8 -b:v 4M"),
        new("av1", 10, false, "av1.mkv", "libsvtav1", "yuv420p10le", "-preset 10"),
        new("hevc", 10, true, "hevc10-hdr10.mkv", "libx265", "yuv420p10le", "-x265-params log-level=none:hdr10=1"),
    ];

    private readonly object _lock = new();
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly EncodingHelper _encodingHelper;
    private readonly HlsCommandBuilder _hlsCommandBuilder;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ITranscodeManager _transcodeManager;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<TranscodeTestManager> _logger;

    private TranscodeTestState _state = TranscodeTestState.Idle;
    private TranscodeTestKind _kind;
    private TranscodeTestRequestDto? _request;
    private DateTime? _startedAt;
    private DateTime? _endedAt;
    private List<TranscodeTestLogLine> _log = [];
    private List<TranscodeTestCheckResult> _checks = [];
    private TranscodeTestStressRun? _stress;
    private CancellationTokenSource? _cancellation;
    private Stopwatch _clock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscodeTestManager"/> class.
    /// </summary>
    /// <param name="configurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    /// <param name="encodingHelper">Instance of <see cref="EncodingHelper"/>.</param>
    /// <param name="hlsCommandBuilder">Instance of <see cref="HlsCommandBuilder"/>.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="transcodeManager">Instance of the <see cref="ITranscodeManager"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TranscodeTestManager}"/> interface.</param>
    public TranscodeTestManager(
        IServerConfigurationManager configurationManager,
        IMediaEncoder mediaEncoder,
        EncodingHelper encodingHelper,
        HlsCommandBuilder hlsCommandBuilder,
        IMediaSourceManager mediaSourceManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ITranscodeManager transcodeManager,
        ISessionManager sessionManager,
        ILogger<TranscodeTestManager> logger)
    {
        _configurationManager = configurationManager;
        _mediaEncoder = mediaEncoder;
        _encodingHelper = encodingHelper;
        _hlsCommandBuilder = hlsCommandBuilder;
        _mediaSourceManager = mediaSourceManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _transcodeManager = transcodeManager;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>
    /// Starts a test.
    /// </summary>
    /// <param name="request">What to test.</param>
    /// <returns><c>false</c> if a test is already running.</returns>
    public bool TryStart(TranscodeTestRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CancellationToken token;
        lock (_lock)
        {
            if (_state == TranscodeTestState.Running)
            {
                return false;
            }

            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            token = _cancellation.Token;
            _state = TranscodeTestState.Running;
            _kind = request.Kind;
            _request = request;
            _startedAt = DateTime.UtcNow;
            _endedAt = null;
            _log = [];
            _checks = [];
            _stress = request.Kind == TranscodeTestKind.StressTest ? new TranscodeTestStressRun(request.MaxStreams) : null;
            _clock = Stopwatch.StartNew();
        }

        _ = Task.Run(() => RunAsync(request, token), CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Stops the running test, or clears the results of the last one.
    /// </summary>
    public void Cancel()
    {
        lock (_lock)
        {
            if (_state == TranscodeTestState.Running)
            {
                _cancellation?.Cancel();
                return;
            }

            _state = TranscodeTestState.Idle;
            _startedAt = null;
            _endedAt = null;
            _log = [];
            _checks = [];
            _stress = null;
        }
    }

    /// <summary>
    /// Gets the state and results so far.
    /// </summary>
    /// <returns>A snapshot of the test.</returns>
    public TranscodeTestStatusDto GetStatus()
    {
        lock (_lock)
        {
            return new TranscodeTestStatusDto
            {
                State = _state,
                Kind = _kind,
                Request = _request,
                StartedAt = _startedAt,
                EndedAt = _endedAt,
                Log = _log.ToArray(),
                Checks = _checks.ToArray(),
                Stress = _stress?.Snapshot(_clock.Elapsed.TotalSeconds)
            };
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
    }

    private async Task RunAsync(TranscodeTestRequestDto request, CancellationToken cancellationToken)
    {
        var state = TranscodeTestState.Completed;
        try
        {
            var options = _configurationManager.GetEncodingOptions();
            Log($"ffmpeg {_mediaEncoder.EncoderVersion} · hardware acceleration: {options.HardwareAccelerationType}");
            var vaapiIdentified = _mediaEncoder.IsVaapiDeviceAmd || _mediaEncoder.IsVaapiDeviceInteliHD || _mediaEncoder.IsVaapiDeviceInteli965;
            if (TranscodeTestRules.NeedsVaapiRestart(options.HardwareAccelerationType, OperatingSystem.IsLinux(), vaapiIdentified))
            {
                Log("Note: Jellyfin has not identified the VAAPI device. It only does that at startup, so if you changed the hardware acceleration type or device, restart Jellyfin and run the test again.");
            }

            var busy = _sessionManager.Sessions.Count(s => s.TranscodingInfo is not null);
            if (busy > 0)
            {
                Log($"Warning: {busy} other transcode(s) are running; results will be lower than the server's full capacity.");
            }

            if (request.Kind == TranscodeTestKind.Compatibility)
            {
                await RunCompatibilityAsync(options, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RunStressTestAsync(request, options, cancellationToken).ConfigureAwait(false);
            }

            Log("Done.");
        }
        catch (OperationCanceledException)
        {
            state = TranscodeTestState.Cancelled;
            _stress?.End(TranscodeTestEndReason.Cancelled, _clock.Elapsed.TotalSeconds);
            Log("Stopped.");
        }
        catch (Exception ex)
        {
            state = TranscodeTestState.Failed;
            _logger.LogError(ex, "Transcoding test failed");
            _stress?.End(TranscodeTestEndReason.Error, _clock.Elapsed.TotalSeconds, error: ex.Message);
            Log("The test failed: " + ex.Message);
        }

        lock (_lock)
        {
            _state = state;
            _endedAt = DateTime.UtcNow;
        }
    }

    // ---- compatibility check -----------------------------------------------------------------

    private async Task RunCompatibilityAsync(EncodingOptions saved, CancellationToken cancellationToken)
    {
        if (saved.HardwareAccelerationType == HardwareAccelerationType.none)
        {
            Log("Hardware acceleration is off, so there is no hardware to check. Choose an acceleration type and save first.");
            return;
        }

        Log("Creating test clips (once; they are kept in the cache folder) …");
        var clips = new List<(ClipSpec Clip, MediaSourceInfo Source)>();
        foreach (var clip in _clips)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (source, error) = await GetClipAsync(clip, cancellationToken).ConfigureAwait(false);
            if (source is null)
            {
                AddCheck(new TranscodeTestCheckResult
                {
                    Kind = clip.Hdr ? TranscodeTestCheckKind.Tonemap : TranscodeTestCheckKind.Decode,
                    Codec = clip.Codec,
                    BitDepth = clip.BitDepth,
                    Hdr = clip.Hdr,
                    Outcome = TranscodeTestOutcome.Skipped,
                    Method = clip.Encoder,
                    Error = error
                });
                continue;
            }

            clips.Add((clip, source));
        }

        Log("Checking hardware decoding …");
        foreach (var (clip, source) in clips.Where(c => !c.Clip.Hdr))
        {
            await CheckDecodeAsync(clip, source, saved, cancellationToken).ConfigureAwait(false);
        }

        var h264 = clips.FirstOrDefault(c => c.Clip.FileName == "h264.mkv").Source;
        if (h264 is not null)
        {
            Log("Checking hardware encoding …");
            foreach (var codec in new[] { "h264", "hevc", "av1" })
            {
                await CheckEncodeAsync(codec, h264, saved, cancellationToken).ConfigureAwait(false);
            }
        }

        var hdr = clips.FirstOrDefault(c => c.Clip.Hdr).Source;
        if (hdr is not null)
        {
            Log("Checking HDR to SDR tone mapping …");
            await CheckTonemapAsync(hdr, saved, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets a test clip, creating it the first time.
    /// </summary>
    /// <returns>The clip, or null and how ffmpeg failed to create it.</returns>
    private async Task<(MediaSourceInfo? Source, string? Error)> GetClipAsync(ClipSpec clip, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(_configurationManager.ApplicationPaths.CachePath, FolderName);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, clip.FileName);
        if (!File.Exists(path))
        {
            // Encoders are not in Jellyfin's capability list, so just try: a missing one makes ffmpeg fail.
            var result = await RunFfmpegAsync(TranscodeTestRules.ClipArguments(clip.Encoder, clip.PixelFormat, clip.ExtraArgs, clip.Hdr, path, false), null, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(path))
            {
                Log($"  Could not create the {clip.FileName} test clip ({clip.Encoder}, {TranscodeTestRules.DescribeExit(result.ExitCode)}): {DescribeErrors(result)}. Trying again at 640x360, which needs far less memory …");
                File.Delete(path);
                result = await RunFfmpegAsync(TranscodeTestRules.ClipArguments(clip.Encoder, clip.PixelFormat, clip.ExtraArgs, clip.Hdr, path, true), null, cancellationToken).ConfigureAwait(false);
            }

            if (result.ExitCode != 0 || !File.Exists(path))
            {
                var error = $"{TranscodeTestRules.DescribeExit(result.ExitCode)}: {DescribeErrors(result)}";
                Log($"  Could not create the {clip.FileName} test clip at 640x360 either ({error})");
                File.Delete(path);
                return (null, error);
            }
        }

        var info = await _mediaEncoder.GetMediaInfo(
            new MediaInfoRequest
            {
                MediaSource = new MediaSourceInfo { Path = path, Protocol = MediaProtocol.File, VideoType = VideoType.VideoFile, Id = clip.FileName },
                MediaType = DlnaProfileType.Video
            },
            cancellationToken).ConfigureAwait(false);
        info.Path = path;
        info.Protocol = MediaProtocol.File;
        info.VideoType = VideoType.VideoFile;
        info.Id = clip.FileName;
        return (info, null);
    }

    private async Task CheckDecodeAsync(ClipSpec clip, MediaSourceInfo source, EncodingOptions saved, CancellationToken cancellationToken)
    {
        var enabled = saved.HardwareDecodingCodecs.Contains(clip.Codec, StringComparison.OrdinalIgnoreCase)
            && !(clip.BitDepth == 10 && clip.Codec == "hevc" && !saved.EnableDecodingColorDepth10Hevc)
            && !(clip.BitDepth == 10 && clip.Codec == "vp9" && !saved.EnableDecodingColorDepth10Vp9);

        // Test what the hardware can do, not only what is switched on.
        var options = CloneOptions(saved);
        options.HardwareDecodingCodecs = _allDecodeCodecs;
        options.EnableDecodingColorDepth10Hevc = true;
        options.EnableDecodingColorDepth10Vp9 = true;

        var result = new TranscodeTestCheckResult
        {
            Kind = TranscodeTestCheckKind.Decode,
            Codec = clip.Codec,
            BitDepth = clip.BitDepth,
            EnabledInSettings = enabled
        };
        var job = CreateJob(source, options, "h264", 1280);
        var hasHardwareDecoder = TranscodeTestRules.UsesHardwareDecoder(_encodingHelper.GetInputArgument(job, options, null));
        var run = hasHardwareDecoder ? await RunJobAsync(job, options, cancellationToken).ConfigureAwait(false) : null;
        if (run is { ExitCode: not 0 } && options.EnableHardwareEncoding)
        {
            // The pipeline also encodes on the GPU. Retry with the CPU encoder, so a broken hardware encoder is not blamed on the decoder.
            Log($"Decoding {clip.Codec} failed. Trying again with the CPU encoder to see whether the decoder is the cause …");
            options.EnableHardwareEncoding = false;
            job = CreateJob(source, options, "h264", 1280);
            run = await RunJobAsync(job, options, cancellationToken).ConfigureAwait(false);
        }

        (result.Outcome, result.Advice) = TranscodeTestRules.Decode(hasHardwareDecoder, run?.ExitCode, run?.InputPixelFormat, enabled);
        result.Fps = run?.ExitCode == 0 ? run.Fps : null;
        result.Error = run?.ExitCode is null or 0 ? null : run.Errors;
        if (result.Outcome == TranscodeTestOutcome.Failed)
        {
            // Not one frame decoded, with the CPU encoder, while H.264 decodes here: the GPU lacks this format. The
            // advice to turn it off stays.
            result.Outcome = TranscodeTestRules.FailedOutcome(ReferencePassed(TranscodeTestCheckKind.Decode), !options.EnableHardwareEncoding, run!.FramesDecoded);
            result.ReferencePassed = result.Outcome == TranscodeTestOutcome.NotSupported;
        }

        if (result.Outcome == TranscodeTestOutcome.Failed)
        {
            // When frames were decoded, a later step such as a filter or the encoder failed, not the decoder's start.
            result.FramesDecoded = run!.FramesDecoded;
            result.FailedAfterDecoding = TranscodeTestRules.FailedAfterDecoding(run.FramesDecoded, run.Errors);
        }

        AddCheck(result);
    }

    private async Task CheckEncodeAsync(string codec, MediaSourceInfo source, EncodingOptions saved, CancellationToken cancellationToken)
    {
        var result = new TranscodeTestCheckResult
        {
            Kind = TranscodeTestCheckKind.Encode,
            Codec = codec,
            BitDepth = 8,
            EnabledInSettings = TranscodeTestRules.IsEncodeEnabled(codec, saved)
        };

        var options = CloneOptions(saved);
        options.EnableHardwareEncoding = true;

        // Decode on the CPU, so a failure here is the encoder's and not the saved hardware decoder's.
        options.HardwareDecodingCodecs = [];
        var job = CreateJob(source, options, codec, 1280);
        var encoder = _encodingHelper.GetVideoEncoder(job, options);
        result.Method = encoder;
        var run = TranscodeTestRules.IsSoftwareEncoder(encoder) ? null : await RunJobAsync(job, options, cancellationToken).ConfigureAwait(false);
        (result.Outcome, result.Advice) = TranscodeTestRules.Encode(encoder, run?.ExitCode, saved.EnableHardwareEncoding, TranscodeTestRules.IsEncodeAllowed(codec, saved));
        result.Fps = run?.ExitCode == 0 ? run.Fps : null;
        result.Error = run?.ExitCode is null or 0 ? null : run.Errors;
        if (result.Outcome == TranscodeTestOutcome.Failed)
        {
            // Not one frame encoded, from a clip decoded on the CPU, while H.264 encodes here: the GPU lacks this
            // format. The advice to turn it off stays.
            result.Outcome = TranscodeTestRules.FailedOutcome(ReferencePassed(TranscodeTestCheckKind.Encode), true, run!.FramesEncoded);
            result.ReferencePassed = result.Outcome == TranscodeTestOutcome.NotSupported;
        }

        AddCheck(result);
    }

    /// <summary>
    /// Gets whether the H.264 8-bit check of a kind passed earlier in this run, so the hardware and its setup work.
    /// </summary>
    private bool ReferencePassed(TranscodeTestCheckKind kind)
    {
        lock (_lock)
        {
            return _checks.Any(c => c.Kind == kind && c.Codec == "h264" && c.BitDepth == 8 && c.Outcome == TranscodeTestOutcome.Passed);
        }
    }

    private async Task CheckTonemapAsync(MediaSourceInfo source, EncodingOptions saved, CancellationToken cancellationToken)
    {
        var savedOn = TonemapFilter(source, saved) is not null;
        var options = CloneOptions(saved);
        options.HardwareDecodingCodecs = _allDecodeCodecs;
        options.EnableDecodingColorDepth10Hevc = true;
        options.EnableTonemapping = true;

        // Keep the saved VPP and VideoToolbox choices. Jellyfin's Intel pipelines prefer VPP over OpenCL when it is on,
        // and on macOS the VideoToolbox option replaces the Metal filter (tonemap_videotoolbox) with the tone mapping
        // built into scale_vt, so forcing either would test a tone mapping filter that playback does not use.
        var job = CreateJob(source, options, "h264", 1920);
        var filter = TonemapFilter(source, options);
        var result = new TranscodeTestCheckResult
        {
            Kind = TranscodeTestCheckKind.Tonemap,
            Codec = "hevc",
            BitDepth = 10,
            Hdr = true,
            EnabledInSettings = savedOn,
            Method = filter?.Split('=')[0]
        };
        var run = filter is null ? null : await RunJobAsync(job, options, cancellationToken).ConfigureAwait(false);
        (result.Outcome, result.Advice) = TranscodeTestRules.Tonemap(filter, run?.ExitCode, savedOn);
        result.Fps = run?.ExitCode == 0 ? run.Fps : null;
        result.Error = run?.ExitCode is null or 0 ? null : run.Errors;
        AddCheck(result);
    }

    private string? TonemapFilter(MediaSourceInfo source, EncodingOptions options)
    {
        var job = CreateJob(source, options, "h264", 1920);
        return TranscodeTestRules.FindTonemapFilter(_encodingHelper.GetVideoProcessingFilterParam(job, options, _encodingHelper.GetVideoEncoder(job, options)));
    }

    private EncodingJobInfo CreateJob(MediaSourceInfo source, EncodingOptions options, string outputCodec, int maxWidth)
    {
        var job = new EncodingJobInfo(TranscodingJobType.Progressive)
        {
            IsVideoRequest = true,
            IsInputVideo = true,
            BaseRequest = new BaseEncodingJobOptions { MaxWidth = maxWidth, VideoCodec = outputCodec, VideoBitRate = 4_000_000 },
            OutputVideoCodec = outputCodec,
            OutputVideoBitrate = 4_000_000
        };
        _encodingHelper.AttachMediaSourceInfo(job, options, source, string.Empty);
        job.VideoType = VideoType.VideoFile;
        return job;
    }

    private async Task<FfmpegRun> RunJobAsync(EncodingJobInfo job, EncodingOptions options, CancellationToken cancellationToken)
    {
        var encoder = _encodingHelper.GetVideoEncoder(job, options);
        var inputModifier = _encodingHelper.GetInputModifier(job, options, null);
        var input = _encodingHelper.GetInputArgument(job, options, null);
        var filters = _encodingHelper.GetVideoProcessingFilterParam(job, options, encoder);

        // The encoder options playback uses (preset, profile, level, bitrate, Intel low-power mode, i915 workaround),
        // so an encoder fails or passes here as it would during playback.
        var quality = _encodingHelper.GetVideoQualityParam(job, encoder, options, EncoderPreset.veryfast);
        var args = string.Create(
            CultureInfo.InvariantCulture,
            $"-hide_banner -v verbose {inputModifier} {input} -map 0:v:0 -an -sn -c:v {encoder}{quality}{filters} -frames:v {CheckFrames} -f null -");
        return await RunFfmpegAsync(args, CheckFrames, cancellationToken).ConfigureAwait(false);
    }

    private static EncodingOptions CloneOptions(EncodingOptions options)
        => JsonSerializer.Deserialize<EncodingOptions>(JsonSerializer.Serialize(options, JsonDefaults.Options), JsonDefaults.Options)
            ?? throw new InvalidOperationException("Could not copy the encoding options.");

    private async Task<FfmpegRun> RunFfmpegAsync(string args, int? frames, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Transcoding test: {Ffmpeg} {Args}", _mediaEncoder.EncoderPath, args);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_mediaEncoder.EncoderPath, args)
            {
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        var timer = Stopwatch.StartNew();
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            cancellationToken.ThrowIfCancellationRequested();
            return new FfmpegRun(-1, null, null, "timed out after 60 s", null, null);
        }

        var output = await stderr.ConfigureAwait(false);
        var elapsed = timer.Elapsed.TotalSeconds;
        var pixelFormat = TranscodeTestRules.DecodedPixelFormat(output);
        return new FfmpegRun(
            process.ExitCode,
            frames.HasValue && elapsed > 0 ? Math.Round(frames.Value / elapsed, 1) : null,
            pixelFormat,
            TranscodeTestRules.ErrorLines(output),
            TranscodeTestRules.FramesDecoded(output),
            TranscodeTestRules.FramesEncoded(output));
    }

    // ---- stress test ------------------------------------------------------------------------

    private async Task RunStressTestAsync(TranscodeTestRequestDto request, EncodingOptions options, CancellationToken cancellationToken)
    {
        var run = _stress!;
        var item = _libraryManager.GetItemById(request.ItemId!.Value) ?? throw new ArgumentException("The item was not found.");
        var codec = request.VideoCodec is "hevc" or "av1" ? request.VideoCodec : "h264";
        // Like playback, write into the transcode folder itself: the startup cleanup task removes empty sub-folders.
        var folder = _configurationManager.GetTranscodePath();
        var prefix = FolderName + "-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        Log(string.Create(CultureInfo.InvariantCulture, $"Stress test: \"{item.Name}\" → {request.MaxWidth} px {codec} {request.VideoBitrate / 1e6:0.#} Mbps."));
        var sourceSeconds = item.RunTimeTicks is long ticks ? TimeSpan.FromTicks(ticks).TotalSeconds : (double?)null;
        run.ShortSource = TranscodeTestRules.IsShortSource(sourceSeconds);
        if (TranscodeTestRules.DescribeShortSource(sourceSeconds) is string shortSource)
        {
            Log(shortSource);
        }

        try
        {
            var first = await StartStreamAsync(run, request, codec, folder, prefix, options, item.Name ?? string.Empty, cancellationToken).ConfigureAwait(false);
            run.SourceFps = first.SourceFps;
            run.ThresholdFps = Math.Round(first.SourceFps * FpsRatio, 3);
            Log(string.Create(CultureInfo.InvariantCulture, $"Each stream must keep {TranscodeTestRules.FormatFps(run.ThresholdFps)} fps ({FpsRatio * 100:0}% of the source's {first.SourceFps:0.###} fps). {TranscodeTestRules.DescribeRamp(RampSeconds, TranscodeTestStream.WindowSeconds)}"));
            await EscalateAsync(run, request, codec, folder, prefix, options, item.Name ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await StopAllAsync(run).ConfigureAwait(false);
            foreach (var file in Directory.EnumerateFiles(folder, prefix + "*"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Could not remove {File}", file);
                }
            }
        }

        var summary = run.Snapshot(_clock.Elapsed.TotalSeconds);
        if (summary.SingleStreamFps is double single)
        {
            Log($"One stream alone ran at {single:0.0} fps ({summary.SingleStreamSpeed:0.00}× real time).");
        }

        Log(TranscodeTestRules.DescribeResult(summary));
    }

    private async Task EscalateAsync(TranscodeTestStressRun run, TranscodeTestRequestDto request, string codec, string folder, string prefix, EncodingOptions options, string itemName, CancellationToken cancellationToken)
    {
        var controller = new TranscodeTestStressController(run.ThresholdFps, RampSeconds, FailSeconds, StartTimeoutSeconds, request.MaxStreams, _clock.Elapsed.TotalSeconds);
        run.SetPhase(TranscodeTestStressPhase.WarmingUp, _clock.Elapsed.TotalSeconds + RampSeconds);
        while (true)
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            var now = _clock.Elapsed.TotalSeconds;
            run.Sample(now);
            foreach (var started in run.Streams.Where(s => !s.FirstFrameLogged).ToList())
            {
                if (started.FirstFrameAt.HasValue && started.DescribeFirstFrame(TranscodeTestProcessIo.BytesRead(started.Process)) is string firstFrame)
                {
                    started.FirstFrameLogged = true;
                    Log(firstFrame);
                }
            }

            foreach (var ended in run.Streams.Where(s => s.HasExited && !s.Stopping && !s.Handled).ToList())
            {
                ended.Handled = true;
                if (ended.ExitCode == 0)
                {
                    // The file ended: start the stream again so the load stays the same.
                    Log($"Stream {ended.Id} reached the end of the file; starting it again.");
                    ended.CollectSegments(true);
                    var restarted = await StartStreamAsync(run, request, codec, folder, prefix, options, itemName, cancellationToken, ended.Id).ConfigureAwait(false);
                    // One stream to the viewer: keep its output and frame rate history, and judge the new process afresh.
                    restarted.CarryOver(ended);
                    controller.StreamRestarted(ended.Id);
                    continue;
                }

                EndStream(run, ended, TranscodeTestEndKind.Exited, controller.MaxStable, now);
                return;
            }

            var readings = run.Streams.Where(s => !s.HasExited).Select(s => new TranscodeTestStreamReading(s.Id, s.Fps(now), now - s.StartedAt, s.ReportedFrames)).ToList();
            var decision = controller.Tick(now, readings);
            run.MaxStable = controller.MaxStable;
            switch (decision.Action)
            {
                case TranscodeTestStressAction.Stalled:
                    // The controller only judges running streams; Last picks the current one when a restart reused the id.
                    EndStream(run, run.Streams.Last(s => s.Id == decision.StreamId!.Value && !s.HasExited), TranscodeTestEndKind.Stalled, controller.MaxStable, now);
                    return;
                case TranscodeTestStressAction.LimitReached:
                    Log($"With {TranscodeTestRules.StreamCount(readings.Count)} running, stream {decision.StreamId} fell to {TranscodeTestRules.FormatFps(TranscodeTestRules.RoundFps(decision.Fps!.Value, run.ThresholdFps))} fps, under {TranscodeTestRules.FormatFps(run.ThresholdFps)} fps, for {FailSeconds:0} s.");
                    run.End(TranscodeTestEndReason.LimitReached, now, decision.StreamId, decision.Fps);
                    return;
                case TranscodeTestStressAction.MaxStreams:
                    Log($"All {request.MaxStreams} streams kept up; that is the limit you set.");
                    run.End(TranscodeTestEndReason.MaxStreams, now);
                    return;
                case TranscodeTestStressAction.AddStream:
                    run.RecordSingleStream(now);
                    await StartStreamAsync(run, request, codec, folder, prefix, options, itemName, cancellationToken).ConfigureAwait(false);
                    now = _clock.Elapsed.TotalSeconds;
                    controller.StreamAdded(now);
                    run.SetPhase(TranscodeTestStressPhase.WarmingUp, now + RampSeconds);
                    break;
                default:
                    if (decision.Phase == TranscodeTestStressPhase.FallingBehind)
                    {
                        if (decision.DipStarted && decision.Fps is double dipFps)
                        {
                            // Once per dip: the wait for the next stream starts again when all keep up.
                            Log(TranscodeTestRules.DescribeDip(decision.StreamId!.Value, dipFps, run.ThresholdFps, RampSeconds, readings.Count < request.MaxStreams ? run.LastStreamId + 1 : null));
                        }

                        run.SetFallingBehind(decision.StreamId!.Value, decision.EndsAt!.Value);
                    }
                    else
                    {
                        run.SetPhase(decision.Phase, decision.NextStreamAt);
                    }

                    break;
            }
        }
    }

    private async Task<TranscodeTestStream> StartStreamAsync(TranscodeTestStressRun run, TranscodeTestRequestDto request, string codec, string folder, string prefix, EncodingOptions options, string itemName, CancellationToken cancellationToken, int? reuseId = null)
    {
        var id = reuseId ?? run.NextStreamId();
        var container = codec == "h264" ? "ts" : "mp4";
        var streamingRequest = new VideoRequestDto
        {
            Id = request.ItemId!.Value,
            MediaSourceId = request.MediaSourceId ?? request.ItemId.Value.ToString("N", CultureInfo.InvariantCulture),
            DeviceId = "transcode-test",
            PlaySessionId = "transcode-test-" + id.ToString(CultureInfo.InvariantCulture),
            VideoCodec = codec,
            AudioCodec = "aac",
            AudioBitRate = 192_000,
            MaxAudioChannels = 2,
            TranscodingMaxAudioChannels = 2,
            MaxWidth = request.MaxWidth,
            VideoBitRate = request.VideoBitrate,
            SegmentContainer = container,
            SegmentLength = 6
        };

        using var state = await StreamingHelpers.GetStreamingState(
                streamingRequest,
                $"/Videos/{streamingRequest.Id:N}/hls1/main/0.{container}",
                new Dictionary<string, string?>(),
                "Jellyfin transcoding test",
                Guid.Empty,
                _mediaSourceManager,
                _userManager,
                _libraryManager,
                _configurationManager,
                _mediaEncoder,
                _encodingHelper,
                _transcodeManager,
                TranscodingJobType.Hls,
                cancellationToken)
            .ConfigureAwait(false);

        var streamPrefix = string.Create(CultureInfo.InvariantCulture, $"{prefix}-s{id}-{run.Streams.Count}_");
        var playlist = Path.Combine(folder, streamPrefix + ".m3u8");
        // Playback's command, plus flags that tag each log line with its level so errors can be told from warnings, and
        // hide the banner. They change only ffmpeg's log text, not the transcode.
        var args = StressLogArguments + _hlsCommandBuilder.GetCommandLineArguments(playlist, state, options, false, 0);
        _logger.LogDebug("Transcoding test stream {Id}: {Ffmpeg} {Args}", id, _mediaEncoder.EncoderPath, args);
        var sourceFps = state.VideoStream?.ReferenceFrameRate ?? 24f;
        if (run.Input is null)
        {
            DescribeStream(run, state, options, args, itemName, sourceFps);
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo(_mediaEncoder.EncoderPath, args)
            {
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        var stream = new TranscodeTestStream(id, process, folder, streamPrefix, sourceFps, _clock.Elapsed.TotalSeconds, state.SegmentLength)
        {
            // A stream restarted at the end of the file is not news, as with its start below.
            FirstFrameLogged = reuseId is not null
        };
        var clock = _clock;
        _ = Task.Run(async () => await stream.ReadProgressAsync(clock).ConfigureAwait(false), CancellationToken.None);
        lock (_lock)
        {
            run.Add(stream);
        }

        if (reuseId is null)
        {
            Log($"Stream {id} started; {run.Streams.Count(s => !s.HasExited)} running.");
        }

        return stream;
    }

    private void DescribeStream(TranscodeTestStressRun run, StreamState state, EncodingOptions options, string args, string itemName, float sourceFps)
    {
        var video = state.VideoStream;
        var tonemap = TranscodeTestRules.FindTonemapFilter(args);
        var encoder = _encodingHelper.GetVideoEncoder(state, options);
        run.Input = new TranscodeTestMediaInfo
        {
            Name = itemName,
            Width = video?.Width,
            Height = video?.Height,
            Codec = video?.Codec,
            BitDepth = video?.BitDepth,
            VideoRangeType = video?.VideoRangeType.ToString(),
            BitrateMbps = state.MediaSource.Bitrate.HasValue ? Math.Round(state.MediaSource.Bitrate.Value / 1e6, 1) : null,
            Fps = Math.Round(sourceFps, 3)
        };
        run.Output = new TranscodeTestOutputInfo
        {
            Width = state.OutputWidth,
            Height = state.OutputHeight,
            Codec = state.ActualOutputVideoCodec ?? string.Empty,
            BitrateMbps = Math.Round((state.OutputVideoBitrate ?? 0) / 1e6, 1),
            AudioCodec = state.ActualOutputAudioCodec,
            AudioChannels = state.OutputAudioChannels,
            Encoder = encoder,
            HardwareDecoding = TranscodeTestRules.UsesHardwareDecoder(args),
            HardwareEncoding = !TranscodeTestRules.IsSoftwareEncoder(encoder),
            Tonemap = TranscodeTestRules.TonemapMode(tonemap)
        };
    }

    private static async Task StopAllAsync(TranscodeTestStressRun run)
    {
        // The output is thrown away, so there is no need to let ffmpeg finish its files.
        var live = run.Streams.Where(s => !s.HasExited).ToList();
        foreach (var s in live)
        {
            s.Stopping = true;
            try
            {
                s.Process.Kill(true);
            }
            catch (InvalidOperationException)
            {
                // It exited on its own.
            }
        }

        foreach (var s in live)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await s.Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var s in run.Streams)
        {
            s.DisposeProcess();
        }
    }

    // ---- shared -----------------------------------------------------------------------------

    /// <summary>
    /// Formats a log line the same way whatever the server's culture, so numbers always use a decimal point.
    /// </summary>
    /// <param name="message">The line.</param>
    /// <returns>The line as text.</returns>
    internal static string FormatLog(FormattableString message) => FormattableString.Invariant(message);

    // Preferred over Log(string) for interpolated strings, which C# would otherwise format with the current culture.
    [OverloadResolutionPriority(1)]
    private void Log(FormattableString message) => Log(FormatLog(message));

    private void Log(string message)
    {
        _logger.LogInformation("Transcoding test: {Message}", message);
        lock (_lock)
        {
            _log.Add(new TranscodeTestLogLine { Seconds = Math.Round(_clock.Elapsed.TotalSeconds, 1), Message = message });
            if (_log.Count > MaxLogLines)
            {
                _log.RemoveAt(0);
            }
        }
    }

    private void AddCheck(TranscodeTestCheckResult result)
    {
        var fps = result.Fps.HasValue ? string.Create(CultureInfo.InvariantCulture, $" at {result.Fps:0} fps") : string.Empty;
        var advice = result.Advice == TranscodeTestAdvice.None ? string.Empty : $"; advice: {result.Advice}";
        Log(FormatLog($"  {result.Kind} {result.Codec} {result.BitDepth}-bit{(result.Hdr ? " HDR" : string.Empty)}: {result.Outcome}{fps}{advice} {result.Method} {result.Error}").TrimEnd());
        lock (_lock)
        {
            _checks.Add(result);
        }
    }

    /// <summary>
    /// Ends a stress test because one stream exited with a non-zero code or produced no frames before the start
    /// timeout, naming the cause ffmpeg gave.
    /// </summary>
    private void EndStream(TranscodeTestStressRun run, TranscodeTestStream stream, TranscodeTestEndKind kind, int maxStable, double now)
    {
        var outcome = TranscodeTestRules.EndOutcome(
            kind,
            run.Output?.Encoder,
            stream.SessionLimitLine,
            stream.GpuMemoryLine,
            maxStable,
            stream.Id == run.LastStreamId,
            stream.ReportedFrames);
        var error = TranscodeTestRules.EndEvidence(
            kind,
            outcome.EndReason,
            outcome.CouldNotStartStream,
            stream.SessionLimitLine,
            stream.GpuMemoryLine,
            stream.ErrorLines);

        var processorTime = ProcessorTime(stream.Process);
        // ffmpeg had not opened the file and barely used the CPU: it was waiting on the storage, not the GPU.
        var waitingOnInput = outcome.EndReason == TranscodeTestEndReason.StreamStalled
            && TranscodeTestRules.IsWaitingOnInput(kind, stream.SawInput, processorTime);

        // The result names only the cause when there is one; the log keeps all of ffmpeg's last error lines.
        var logged = string.IsNullOrEmpty(error)
            ? error
            : TranscodeTestRules.WithCause(TranscodeTestRules.EndCause(outcome.EndReason, stream.SessionLimitLine, stream.GpuMemoryLine), stream.ErrorLines);
        if (kind == TranscodeTestEndKind.Exited)
        {
            if (string.IsNullOrEmpty(logged))
            {
                Log($"Stream {stream.Id} failed (exit {stream.ExitCode}). ffmpeg printed no error.");
            }
            else
            {
                Log($"Stream {stream.Id} failed (exit {stream.ExitCode}): {logged}");
            }
        }
        else
        {
            // ffmpeg's last lines help diagnose a hang, but they are not its error, so they only go in the log. When it
            // printed nothing, the diagnostics line says so.
            var output = stream.StallEvidence;
            if (!string.IsNullOrEmpty(logged))
            {
                Log($"Stream {stream.Id} produced no frames in {StartTimeoutSeconds:0} s. ffmpeg's error: {logged}");
            }
            else if (!string.IsNullOrEmpty(output))
            {
                Log($"Stream {stream.Id} produced no frames in {StartTimeoutSeconds:0} s. ffmpeg printed no error. Its last output: {output}");
            }
            else if (!stream.PrintedNothing)
            {
                Log($"Stream {stream.Id} produced no frames in {StartTimeoutSeconds:0} s. ffmpeg printed no error.");
            }

            // Read before the process is stopped.
            Log(stream.StallDiagnostics(now, processorTime, TranscodeTestProcessIo.BytesRead(stream.Process)));
        }

        if (outcome.EndReason == TranscodeTestEndReason.NvencSessionLimit)
        {
            LogNvencRefusal(stream.Id);
        }
        else if (outcome.EndReason == TranscodeTestEndReason.GpuMemoryFull)
        {
            Log($"The GPU ran out of memory on stream {stream.Id}. {maxStable} stream(s) kept up with real time before that; memory, not speed, is the limit here.");
        }
        else if (waitingOnInput)
        {
            Log(TranscodeTestRules.DescribeWaitingOnInput(outcome.CouldNotStartStream ? maxStable : null, stream.Id, StartTimeoutSeconds, processorTime.GetValueOrDefault()));
        }
        else if (outcome.CouldNotStartStream)
        {
            Log(TranscodeTestRules.DescribeCouldNotStart(kind, maxStable, stream.Id, StartTimeoutSeconds));
        }

        run.End(
            outcome.EndReason,
            now,
            stream.Id,
            error: error,
            couldNotStartStream: outcome.CouldNotStartStream,
            waitingOnInput: waitingOnInput);
    }

    private void LogNvencRefusal(int streamId)
        => Log($"NVIDIA's driver refused stream {streamId}. Consumer NVIDIA GPUs allow a limited number of encoding sessions at once (from 3 on older drivers to 12 on recent ones). That is a driver limit, not what the GPU can do.");

    private static TimeSpan? ProcessorTime(Process process)
    {
        try
        {
            return process.TotalProcessorTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static string DescribeErrors(FfmpegRun run) => string.IsNullOrEmpty(run.Errors) ? "no error message" : run.Errors;

    private sealed record ClipSpec(string Codec, int BitDepth, bool Hdr, string FileName, string Encoder, string PixelFormat, string ExtraArgs);

    private sealed record FfmpegRun(int ExitCode, double? Fps, string? InputPixelFormat, string Errors, int? FramesDecoded, int? FramesEncoded);
}
