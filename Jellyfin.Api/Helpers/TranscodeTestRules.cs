using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Api.Models.TranscodeTestDtos;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// How the transcoding test reads ffmpeg command lines and output, and what it concludes from them.
/// </summary>
internal static partial class TranscodeTestRules
{
    /// <summary>
    /// CPU seconds under which a stalled stream that never opened its file is taken to have been waiting on reading
    /// it. Decoding even one 4K frame takes longer; probing a file that reads quickly takes far less.
    /// </summary>
    internal const double WaitingOnInputCpuSeconds = 3;

    /// <summary>
    /// The fraction of the threshold, and of the peak combined frame rate, under which streams at the limit are taken
    /// to have collapsed together rather than one falling a little behind.
    /// </summary>
    internal const double CollapseFraction = 0.25;

    // How ffmpeg says the GPU ran out of memory: CUDA (NVDEC, cuvid, CUDA filters) through cuda_check.h; VAAPI through
    // libva's vaErrorStr for VA_STATUS_ERROR_ALLOCATION_FAILED; QSV MFX_ERR_MEMORY_ALLOC through ff_qsv_print_error;
    // Vulkan and libplacebo through ff_vk_ret2str.
    private static readonly string[] _gpuMemoryPatterns =
    [
        "CUDA_ERROR_OUT_OF_MEMORY",
        "(resource allocation failed)",
        "failed to allocate memory (-4)",
        "VK_ERROR_OUT_OF_DEVICE_MEMORY",
    ];

    private static readonly string[] _hardwareFrameFormats = ["videotoolbox_vld", "cuda", "qsv", "vaapi", "d3d11", "dxva2_vld", "drm_prime", "vulkan", "opencl"];

    /// <summary>
    /// Gets whether ffmpeg arguments ask for a hardware decoder.
    /// </summary>
    /// <param name="arguments">The ffmpeg arguments.</param>
    /// <returns><c>true</c> if they use a hardware decoder.</returns>
    public static bool UsesHardwareDecoder(string arguments)
        => arguments.Contains("-hwaccel ", StringComparison.Ordinal) || HardwareDecoderRegex().IsMatch(arguments);

    /// <summary>
    /// Gets the pixel format of the decoded frames from ffmpeg's verbose output.
    /// </summary>
    /// <param name="output">ffmpeg's standard error at <c>-v verbose</c>.</param>
    /// <returns>The pixel format, or <c>null</c> if it is not in the output.</returns>
    public static string? DecodedPixelFormat(string output)
        => InputFormatRegex().Match(output) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>
    /// Gets whether a pixel format is a hardware frame format, meaning the frames were decoded on the GPU.
    /// </summary>
    /// <param name="pixelFormat">The pixel format.</param>
    /// <returns><c>true</c> for hardware frames.</returns>
    public static bool IsHardwareFrameFormat(string? pixelFormat)
        => pixelFormat is not null && _hardwareFrameFormats.Contains(pixelFormat, StringComparer.Ordinal);

    /// <summary>
    /// Finds the tone mapping filter in a filter chain or command line.
    /// </summary>
    /// <param name="filters">The filter chain or command line.</param>
    /// <returns>The filter and its options, or <c>null</c> when nothing tone-maps.</returns>
    public static string? FindTonemapFilter(string filters)
        => TonemapFilterRegex().Match(filters) is { Success: true } match ? match.Value : null;

    /// <summary>
    /// Gets where a tone mapping filter runs.
    /// </summary>
    /// <param name="filter">The filter from <see cref="FindTonemapFilter"/>.</param>
    /// <returns>How HDR is tone-mapped.</returns>
    public static TranscodeTestTonemapMode TonemapMode(string? filter)
    {
        if (filter is null)
        {
            return TranscodeTestTonemapMode.None;
        }

        return filter.StartsWith("tonemapx", StringComparison.Ordinal) || filter.StartsWith("zscale", StringComparison.Ordinal)
            ? TranscodeTestTonemapMode.Software
            : TranscodeTestTonemapMode.Hardware;
    }

    /// <summary>
    /// Concludes a hardware decoding check.
    /// </summary>
    /// <param name="hasHardwareDecoder">Whether Jellyfin would use a hardware decoder for the clip.</param>
    /// <param name="exitCode">ffmpeg's exit code, when it ran.</param>
    /// <param name="decodedPixelFormat">The pixel format of the decoded frames.</param>
    /// <param name="enabled">Whether hardware decoding of the format is on in the settings.</param>
    /// <returns>The outcome and the advice.</returns>
    public static (TranscodeTestOutcome Outcome, TranscodeTestAdvice Advice) Decode(bool hasHardwareDecoder, int? exitCode, string? decodedPixelFormat, bool enabled)
    {
        TranscodeTestOutcome outcome;
        if (!hasHardwareDecoder)
        {
            outcome = TranscodeTestOutcome.NotSupported;
        }
        else if (exitCode != 0)
        {
            outcome = TranscodeTestOutcome.Failed;
        }
        else if (IsHardwareFrameFormat(decodedPixelFormat))
        {
            outcome = TranscodeTestOutcome.Passed;
        }
        else
        {
            // ffmpeg quietly fell back to the CPU.
            outcome = TranscodeTestOutcome.Software;
        }

        var advice = (outcome, enabled) switch
        {
            (TranscodeTestOutcome.Passed, false) => TranscodeTestAdvice.TurnOn,
            (TranscodeTestOutcome.Passed, true) => TranscodeTestAdvice.None,
            (_, true) => TranscodeTestAdvice.TurnOff,
            _ => TranscodeTestAdvice.None
        };
        return (outcome, advice);
    }

    /// <summary>
    /// Gets how many frames ffmpeg decoded, from its final statistics at <c>-v verbose</c>.
    /// </summary>
    /// <param name="output">ffmpeg's standard error.</param>
    /// <returns>The most frames any input stream decoded, or <c>null</c> when ffmpeg did not say.</returns>
    public static int? FramesDecoded(string output)
    {
        int? frames = null;
        foreach (Match match in FramesDecodedRegex().Matches(output))
        {
            if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                frames = Math.Max(frames ?? 0, n);
            }
        }

        return frames;
    }

    /// <summary>
    /// Gets whether a failed decoding check decoded frames before it failed, so a later step of the pipeline, such as
    /// a filter or the encoder, failed and not the start of decoding.
    /// </summary>
    /// <param name="framesDecoded">How many frames ffmpeg decoded, see <see cref="FramesDecoded"/>.</param>
    /// <param name="errors">ffmpeg's error lines.</param>
    /// <returns><c>true</c> when frames were decoded, or, when ffmpeg did not say, when the error came from an output
    /// stream (<c>vost#</c>).</returns>
    public static bool FailedAfterDecoding(int? framesDecoded, string? errors)
        => framesDecoded is int frames
            ? frames > 0
            : errors is not null && errors.Contains("[vost#", StringComparison.Ordinal);

    /// <summary>
    /// Gets how many frames ffmpeg encoded, from its final statistics at <c>-v verbose</c>.
    /// </summary>
    /// <param name="output">ffmpeg's standard error.</param>
    /// <returns>The most frames any output stream encoded, or <c>null</c> when ffmpeg did not say.</returns>
    public static int? FramesEncoded(string output)
    {
        int? frames = null;
        foreach (Match match in FramesEncodedRegex().Matches(output))
        {
            if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                frames = Math.Max(frames ?? 0, n);
            }
        }

        return frames;
    }

    /// <summary>
    /// Gets how many frames an encoder got out. When ffmpeg gave no count, the encoder is taken to have got none out
    /// only if ffmpeg's progress never passed frame 0 and an error names this encoder, its output stream, or opening
    /// the encoder or output stream.
    /// </summary>
    /// <param name="output">ffmpeg's standard error.</param>
    /// <param name="encoder">The encoder under test.</param>
    /// <returns>The frames encoded, 0 when the encoder failed before any output, or <c>null</c> when ffmpeg did not
    /// say.</returns>
    public static int? EncodedFrames(string output, string encoder)
    {
        if (FramesEncoded(output) is int frames)
        {
            return frames;
        }

        foreach (Match match in ProgressFramesRegex().Matches(output))
        {
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > 0)
            {
                return null;
            }
        }

        var lines = output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Any(l => IsEncoderError(l, encoder)) ? 0 : null;
    }

    private static bool IsEncoderError(string line, string encoder)
    {
        var named = false;
        foreach (Match match in StreamTagRegex().Matches(line))
        {
            if (!string.Equals(match.Groups[1].Value, encoder, StringComparison.Ordinal))
            {
                // About another stream or encoder.
                return false;
            }

            named = true;
        }

        return named
            ? line.Contains("rror", StringComparison.Ordinal) || line.Contains("ailed", StringComparison.Ordinal) || line.Contains("Could not open", StringComparison.Ordinal)
            : line.Contains("Could not open encoder", StringComparison.Ordinal)
                || line.Contains("Error while opening encoder", StringComparison.Ordinal)
                || line.Contains("Error initializing output stream", StringComparison.Ordinal);
    }

    /// <summary>
    /// Concludes a decoding or encoding check that failed. It means the hardware lacks the format, not that the setup is
    /// broken, only when the H.264 8-bit check of the same kind passed in this run, the other half of the pipeline was
    /// ruled out, and ffmpeg said it got not a single frame through.
    /// </summary>
    /// <param name="referencePassed">Whether the H.264 8-bit check of the same kind passed earlier in this run.</param>
    /// <param name="otherHalfRuledOut">For decoding, whether the failed run encoded on the CPU; for encoding, always
    /// <c>true</c>, as it decodes on the CPU.</param>
    /// <param name="frames">The frames ffmpeg decoded or encoded, or <c>null</c> when it did not say.</param>
    /// <returns><see cref="TranscodeTestOutcome.NotSupported"/> when the hardware lacks the format, otherwise
    /// <see cref="TranscodeTestOutcome.Failed"/>.</returns>
    public static TranscodeTestOutcome FailedOutcome(bool referencePassed, bool otherHalfRuledOut, int? frames)
        => referencePassed && otherHalfRuledOut && frames == 0 ? TranscodeTestOutcome.NotSupported : TranscodeTestOutcome.Failed;

    /// <summary>
    /// Gets whether the stress test can play an item: any video, such as a movie, an episode, a music video or a home
    /// video.
    /// </summary>
    /// <param name="item">The item, or <c>null</c> when it was not found.</param>
    /// <returns><c>true</c> for a video.</returns>
    public static bool IsStressTestItem(BaseItem? item) => item is Video;

    /// <summary>
    /// Describes a source short enough that the stress test restarts its streams often, for the log.
    /// </summary>
    /// <param name="seconds">The source's run time, or <c>null</c> when it is not known.</param>
    /// <returns>The line for the log, or <c>null</c> when the source is long enough or its length is not known.</returns>
    public static string? DescribeShortSource(double? seconds)
        => IsShortSource(seconds)
            ? string.Create(CultureInfo.InvariantCulture, $"This file is {seconds:0.#} s long. Each stream restarts at its end, which briefly disturbs the measurement; a file of at least a minute gives steadier results.")
            : null;

    /// <summary>
    /// Gets whether a source is shorter than <see cref="TranscodeTestManager.ShortSourceSeconds"/>.
    /// </summary>
    /// <param name="seconds">The source's run time, or <c>null</c> when it is not known.</param>
    /// <returns><c>true</c> for a known run time under the minimum.</returns>
    public static bool IsShortSource(double? seconds) => seconds is double s && s > 0 && s < TranscodeTestManager.ShortSourceSeconds;

    /// <summary>
    /// Gets whether the saved settings let Jellyfin pick an output codec. Jellyfin only prefers HEVC and AV1 when
    /// "Allow encoding in HEVC/AV1 format" is on, whatever the hardware can do.
    /// </summary>
    /// <param name="codec">The output codec.</param>
    /// <param name="saved">The saved encoding options.</param>
    /// <returns><c>false</c> for HEVC or AV1 when their "Allow" option is off, otherwise <c>true</c>.</returns>
    public static bool IsEncodeAllowed(string codec, EncodingOptions saved) => codec.ToLowerInvariant() switch
    {
        "hevc" or "h265" => saved.AllowHevcEncoding,
        "av1" => saved.AllowAv1Encoding,
        _ => true
    };

    /// <summary>
    /// Gets whether the saved settings would encode to a codec on the GPU.
    /// </summary>
    /// <param name="codec">The output codec.</param>
    /// <param name="saved">The saved encoding options.</param>
    /// <returns><c>true</c> if hardware encoding is on and the codec is allowed.</returns>
    public static bool IsEncodeEnabled(string codec, EncodingOptions saved)
        => saved.EnableHardwareEncoding && IsEncodeAllowed(codec, saved);

    /// <summary>
    /// Concludes a hardware encoding check.
    /// </summary>
    /// <param name="encoder">The encoder Jellyfin chose with hardware encoding on.</param>
    /// <param name="exitCode">ffmpeg's exit code, when it ran.</param>
    /// <param name="hardwareEncodingOn">Whether "Enable hardware encoding" is on in the settings.</param>
    /// <param name="codecAllowed">Whether the codec is allowed in the settings, see <see cref="IsEncodeAllowed"/>.</param>
    /// <returns>The outcome and the advice.</returns>
    public static (TranscodeTestOutcome Outcome, TranscodeTestAdvice Advice) Encode(string encoder, int? exitCode, bool hardwareEncodingOn, bool codecAllowed)
    {
        if (IsSoftwareEncoder(encoder))
        {
            return (TranscodeTestOutcome.NotSupported, TranscodeTestAdvice.None);
        }

        // Never advise allowing HEVC or AV1: that depends on the clients, not on the hardware.
        if (exitCode == 0)
        {
            return (TranscodeTestOutcome.Passed, !hardwareEncodingOn && codecAllowed ? TranscodeTestAdvice.TurnOn : TranscodeTestAdvice.None);
        }

        return (TranscodeTestOutcome.Failed, hardwareEncodingOn && codecAllowed ? TranscodeTestAdvice.TurnOff : TranscodeTestAdvice.None);
    }

    /// <summary>
    /// Concludes a tone mapping check.
    /// </summary>
    /// <param name="filter">The tone mapping filter with tone mapping on, or <c>null</c> if there is none.</param>
    /// <param name="exitCode">ffmpeg's exit code, when it ran.</param>
    /// <param name="enabled">Whether the saved settings tone-map.</param>
    /// <returns>The outcome and the advice.</returns>
    public static (TranscodeTestOutcome Outcome, TranscodeTestAdvice Advice) Tonemap(string? filter, int? exitCode, bool enabled)
    {
        if (filter is null)
        {
            return (TranscodeTestOutcome.NotSupported, TranscodeTestAdvice.None);
        }

        if (exitCode != 0)
        {
            return (TranscodeTestOutcome.Failed, enabled ? TranscodeTestAdvice.TurnOff : TranscodeTestAdvice.None);
        }

        var outcome = TonemapMode(filter) == TranscodeTestTonemapMode.Software ? TranscodeTestOutcome.Software : TranscodeTestOutcome.Passed;
        return (outcome, enabled ? TranscodeTestAdvice.None : TranscodeTestAdvice.TurnOn);
    }

    /// <summary>
    /// Picks the lines of ffmpeg's output that explain a failure.
    /// </summary>
    /// <param name="output">ffmpeg's standard error.</param>
    /// <returns>Up to the last three error lines, joined with " | ".</returns>
    public static string ErrorLines(string output)
        => string.Join(
            " | ",
            output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Contains("rror", StringComparison.Ordinal) || l.Contains("ailed", StringComparison.Ordinal))
                // Encoder libraries print harmless warnings such as SVT-AV1's "Svt[warn]: Failed to set thread priority".
                .Where(l => !l.Contains("[warn", StringComparison.OrdinalIgnoreCase))
                .TakeLast(3));

    /// <summary>
    /// Describes how ffmpeg ended, for a failure that may have printed nothing.
    /// </summary>
    /// <param name="exitCode">ffmpeg's exit code.</param>
    /// <returns>The exit code, and the signal when the process was killed by one, with what the common ones mean.</returns>
    public static string DescribeExit(int exitCode)
    {
        if (exitCode is not (> 128 and < 160))
        {
            return string.Create(CultureInfo.InvariantCulture, $"exit code {exitCode}");
        }

        var signal = exitCode - 128;
        var meaning = signal switch
        {
            4 => ", illegal instruction (the CPU may lack an instruction this encoder needs)",
            9 => ", killed (often because it ran out of memory)",
            _ => string.Empty
        };
        return string.Create(CultureInfo.InvariantCulture, $"exit code {exitCode}, ended by signal {signal}{meaning}");
    }

    /// <summary>
    /// Gets whether to remind the administrator to restart Jellyfin before testing VAAPI.
    /// </summary>
    /// <remarks>
    /// Jellyfin identifies the VAAPI device only at startup, and only when VAAPI is the saved acceleration type.
    /// Until then it uses its generic VAAPI pipeline, so results differ from those after a restart.
    /// </remarks>
    /// <param name="type">The saved hardware acceleration type.</param>
    /// <param name="isLinux">Whether the server runs on Linux, the only system where Jellyfin identifies the device.</param>
    /// <param name="deviceIdentified">Whether Jellyfin identified the device as AMD or Intel.</param>
    /// <returns><c>true</c> when the device was not identified.</returns>
    public static bool NeedsVaapiRestart(HardwareAccelerationType type, bool isLinux, bool deviceIdentified)
        => type == HardwareAccelerationType.vaapi && isLinux && !deviceIdentified;

    /// <summary>
    /// Builds the ffmpeg arguments that make a test clip from ffmpeg's test pattern.
    /// </summary>
    /// <remarks>
    /// The smaller form is a retry for small servers. At 1080p, libsvtav1 needs more than 512 MB, so a container with a
    /// tight memory limit kills it, or it crashes. At 640x360 it fits in 256 MB, and the clip only has to exercise the
    /// decoder. Thread options are left alone: "-threads 1" does not lower libsvtav1's memory, and its "lp=1"
    /// parameter crashes the macOS build.
    /// </remarks>
    /// <param name="encoder">The software encoder.</param>
    /// <param name="pixelFormat">The clip's pixel format.</param>
    /// <param name="extraArgs">Encoder options.</param>
    /// <param name="hdr">Whether to tag the clip as HDR10.</param>
    /// <param name="path">Where to write the clip.</param>
    /// <param name="smaller">Whether to make the 640x360 clip.</param>
    /// <returns>The arguments.</returns>
    public static string ClipArguments(string encoder, string pixelFormat, string extraArgs, bool hdr, string path, bool smaller)
    {
        var size = smaller ? "640x360" : "1920x1080";
        var hdrArgs = hdr ? "-vf setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc " : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"-hide_banner -v error -y -f lavfi -i testsrc2=size={size}:rate=24 -t 4 {hdrArgs}-c:v {encoder} -pix_fmt {pixelFormat} {extraArgs} \"{path}\"");
    }

    /// <summary>
    /// Gets whether a failed stream was refused by NVIDIA's limit on concurrent encoding sessions.
    /// </summary>
    /// <remarks>
    /// Consumer GPUs allow a limited number of sessions at once (from 3 on older drivers to 12 on recent ones). The driver refuses the next
    /// one when the session opens: "OpenEncodeSessionEx failed: out of memory (10)", or "incompatible client key (21)"
    /// on older drivers.
    /// </remarks>
    /// <param name="encoder">The stream's video encoder.</param>
    /// <param name="errors">ffmpeg's error lines.</param>
    /// <returns><c>true</c> for an NVENC encoder that failed with one of those errors.</returns>
    public static bool IsNvencSessionLimit(string? encoder, string? errors)
        => IsNvencEncoder(encoder) && IsSessionLimitText(errors);

    /// <summary>
    /// Gets whether text holds NVIDIA's session refusal, whatever the encoder.
    /// </summary>
    /// <param name="text">ffmpeg output.</param>
    /// <returns><c>true</c> for "out of memory (10)" or "incompatible client key (21)".</returns>
    internal static bool IsSessionLimitText(string? text)
        => text is not null
            && (text.Contains("out of memory (10)", StringComparison.OrdinalIgnoreCase)
                || text.Contains("incompatible client key (21)", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gets whether an ffmpeg line says the GPU ran out of memory.
    /// </summary>
    /// <remarks>
    /// Each pattern is how ffmpeg prints device memory exhaustion for one backend. Generic text such as "Cannot
    /// allocate memory" is left out, because it is also printed for host memory. NVIDIA's session refusal, which says
    /// "out of memory (10)", is never a GPU memory line.
    /// </remarks>
    /// <param name="line">One line of ffmpeg output.</param>
    /// <returns><c>true</c> when the line matches a GPU memory pattern.</returns>
    internal static bool IsGpuMemoryLine(string? line)
        => line is not null
            && !IsSessionLimitText(line)
            && _gpuMemoryPatterns.Any(p => line.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Concludes a stress-test stream that produced no frames before the start timeout.
    /// </summary>
    /// <remarks>
    /// The same rules as <see cref="EndOutcome"/> for a stall, with the stream's evidence as the session text and no
    /// GPU memory line.
    /// </remarks>
    /// <param name="encoder">The streams' video encoder.</param>
    /// <param name="evidence">The stalled stream's last ffmpeg output.</param>
    /// <param name="maxStable">The most streams that all kept up.</param>
    /// <param name="isNewest">Whether the stalled stream is the most recently added one.</param>
    /// <returns>Why the test ended, and whether the server could not start one more stream.</returns>
    public static (TranscodeTestEndReason EndReason, bool CouldNotStartStream) StallOutcome(string? encoder, string? evidence, int maxStable, bool isNewest)
        => EndOutcome(TranscodeTestEndKind.Stalled, encoder, evidence, null, maxStable, isNewest, false);

    /// <summary>
    /// Concludes a stress-test stream that exited with a non-zero code or stalled.
    /// </summary>
    /// <remarks>
    /// NVIDIA's session refusal is matched first, whatever came before or after it. Then a GPU memory error after at
    /// least one stream kept up means memory was the limit: the kept-up count is the result. Otherwise, the newest
    /// stream ending before its first frame after at least one stream kept up means the server could not start one
    /// more stream. A silent hang is never blamed on NVIDIA's session limit: drivers refuse a session with an error and
    /// exit, and that refusal is matched first.
    /// </remarks>
    /// <param name="kind">How the stream ended.</param>
    /// <param name="encoder">The streams' video encoder.</param>
    /// <param name="sessionLimitLine">The stream's first line with NVIDIA's session refusal text, or null.</param>
    /// <param name="gpuMemoryLine">The stream's first line saying the GPU ran out of memory, or null.</param>
    /// <param name="maxStable">The most streams that all kept up.</param>
    /// <param name="isNewest">Whether the stream is the most recently added one.</param>
    /// <param name="reportedFrames">Whether the stream reported at least one encoded frame.</param>
    /// <returns>Why the test ended, and whether the server could not start one more stream.</returns>
    internal static (TranscodeTestEndReason EndReason, bool CouldNotStartStream) EndOutcome(
        TranscodeTestEndKind kind,
        string? encoder,
        string? sessionLimitLine,
        string? gpuMemoryLine,
        int maxStable,
        bool isNewest,
        bool reportedFrames)
    {
        if (IsNvencSessionLimit(encoder, sessionLimitLine))
        {
            return (TranscodeTestEndReason.NvencSessionLimit, false);
        }

        if (gpuMemoryLine is not null && maxStable >= 1)
        {
            return (TranscodeTestEndReason.GpuMemoryFull, false);
        }

        // Only a stream that never reported a frame never started, whether it then exited or stalled.
        var neverStarted = !reportedFrames;
        var couldNotStart = isNewest && maxStable >= 1 && neverStarted;
        var reason = kind == TranscodeTestEndKind.Stalled ? TranscodeTestEndReason.StreamStalled : TranscodeTestEndReason.StreamFailed;
        return (reason, couldNotStart);
    }

    /// <summary>
    /// Describes the newest stream not starting after others kept up.
    /// </summary>
    /// <param name="kind">How the stream ended: an exit before its first frame, or no frames before the timeout.</param>
    /// <param name="maxStable">The most streams that all kept up.</param>
    /// <param name="streamId">The stream that did not start.</param>
    /// <param name="startTimeoutSeconds">How long a stream may take to report its first frame.</param>
    /// <returns>The sentence for the log.</returns>
    internal static string DescribeCouldNotStart(TranscodeTestEndKind kind, int maxStable, int streamId, double startTimeoutSeconds)
        => kind == TranscodeTestEndKind.Stalled
            ? string.Create(CultureInfo.InvariantCulture, $"{maxStable} stream(s) kept up; stream {streamId} started but produced no frames in {startTimeoutSeconds:0} s.")
            : string.Create(CultureInfo.InvariantCulture, $"{maxStable} stream(s) kept up; the server could not start stream {streamId}.");

    /// <summary>
    /// Gets whether a stream that produced no frames was waiting rather than working: ffmpeg had not opened the file,
    /// so it was still probing it, and it used little CPU time, so it was waiting on reading the media.
    /// </summary>
    /// <param name="kind">How the stream ended.</param>
    /// <param name="sawInput">Whether ffmpeg said it opened the file.</param>
    /// <param name="processorTime">ffmpeg's CPU time, or null when it could not be read.</param>
    /// <returns><c>true</c> for a stall before the file was opened, with less than
    /// <see cref="WaitingOnInputCpuSeconds"/> of CPU time.</returns>
    internal static bool IsWaitingOnInput(TranscodeTestEndKind kind, bool sawInput, TimeSpan? processorTime)
        => kind == TranscodeTestEndKind.Stalled && !sawInput && processorTime is TimeSpan p && p.TotalSeconds < WaitingOnInputCpuSeconds;

    /// <summary>
    /// Describes a stream that did not start because ffmpeg was waiting on reading the file.
    /// </summary>
    /// <param name="maxStable">The most streams that all kept up, or null when the stream is not a capacity result.</param>
    /// <param name="streamId">The stream that did not start.</param>
    /// <param name="startTimeoutSeconds">How long a stream may take to report its first frame.</param>
    /// <param name="processorTime">ffmpeg's CPU time.</param>
    /// <returns>The sentence for the log.</returns>
    internal static string DescribeWaitingOnInput(int? maxStable, int streamId, double startTimeoutSeconds, TimeSpan processorTime)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"Stream {streamId} did not start within {startTimeoutSeconds:0} s: ffmpeg had not opened the file yet and used {processorTime.TotalSeconds:0.0} s of CPU time, so it was waiting, probably on reading the media. Slow or shared storage, not the GPU, may be the limit.");
        return maxStable is int kept ? string.Create(CultureInfo.InvariantCulture, $"{kept} stream(s) kept up. {text}") : text;
    }

    /// <summary>
    /// Gets the line that names why a stress test ended, for the endings that have one.
    /// </summary>
    /// <param name="reason">Why the test ended.</param>
    /// <param name="sessionLimitLine">The stream's first session refusal line, or null.</param>
    /// <param name="gpuMemoryLine">The stream's first GPU memory line, or null.</param>
    /// <returns>The session refusal for the driver limit, the memory line for full GPU memory, otherwise null.</returns>
    internal static string? EndCause(TranscodeTestEndReason reason, string? sessionLimitLine, string? gpuMemoryLine) => reason switch
    {
        TranscodeTestEndReason.NvencSessionLimit => sessionLimitLine,
        TranscodeTestEndReason.GpuMemoryFull => gpuMemoryLine,
        _ => null
    };

    /// <summary>
    /// Picks the error text for the stream that ended a stress test.
    /// </summary>
    /// <param name="kind">How the stream ended.</param>
    /// <param name="reason">Why the test ended.</param>
    /// <param name="couldNotStart">Whether the server could not start the stream.</param>
    /// <param name="sessionLimitLine">The stream's first session refusal line, or null.</param>
    /// <param name="gpuMemoryLine">The stream's first GPU memory line, or null.</param>
    /// <param name="errorLines">The stream's last error lines.</param>
    /// <returns>Only the matched line for the driver limit and full GPU memory; the other error lines stay in the
    /// server log. Otherwise ffmpeg's error lines joined with " | ". Other output, such as warnings, is never returned
    /// as the error. Empty for a plain failure without error lines, and null for a stream that never started without
    /// them.</returns>
    internal static string? EndEvidence(
        TranscodeTestEndKind kind,
        TranscodeTestEndReason reason,
        bool couldNotStart,
        string? sessionLimitLine,
        string? gpuMemoryLine,
        IReadOnlyList<string> errorLines)
    {
        var cause = EndCause(reason, sessionLimitLine, gpuMemoryLine);
        if (!string.IsNullOrEmpty(cause))
        {
            return cause;
        }

        // A plain failure keeps its error lines even when empty, as before; a stream that never started has null.
        var text = string.Join(" | ", errorLines);
        return (kind == TranscodeTestEndKind.Exited && !couldNotStart) || !string.IsNullOrEmpty(text) ? text : null;
    }

    /// <summary>
    /// Puts the line that names the cause before the kept lines, unless one of them is that line.
    /// </summary>
    /// <param name="cause">The matched line, or null.</param>
    /// <param name="kept">The kept error lines.</param>
    /// <returns>The lines joined with " | ", with the cause once.</returns>
    internal static string WithCause(string? cause, IReadOnlyList<string> kept)
        => string.IsNullOrEmpty(cause) || kept.Contains(cause, StringComparer.Ordinal)
            ? string.Join(" | ", kept)
            : string.Join(" | ", kept.Prepend(cause));

    private static bool IsNvencEncoder(string? encoder)
        => encoder is not null && encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Names why a stress test ended the way the Jellyfin hardware survey (hwa.jellyfin.org) does, so results can be
    /// compared with it.
    /// </summary>
    /// <param name="reason">Why the test ended.</param>
    /// <returns><c>performance</c>, <c>failed_timeout</c> or <c>failed_nvenc_limit</c>; <c>null</c> for an ending the survey has no name for.</returns>
    public static string? SurveyFailureReason(TranscodeTestEndReason reason) => reason switch
    {
        TranscodeTestEndReason.LimitReached => "performance",
        TranscodeTestEndReason.StreamStalled => "failed_timeout",
        TranscodeTestEndReason.NvencSessionLimit => "failed_nvenc_limit",
        _ => null
    };

    /// <summary>
    /// Gets whether an ffmpeg encoder runs on the CPU.
    /// </summary>
    /// <param name="encoder">The encoder name.</param>
    /// <returns><c>true</c> for software encoders such as libx264.</returns>
    public static bool IsSoftwareEncoder(string encoder) => encoder.StartsWith("lib", StringComparison.Ordinal);

    /// <summary>
    /// Rounds a stress test frame rate reading to one decimal, or to two or three when fewer would move it onto or
    /// across the threshold, so a reading under the threshold never looks equal to it or above it.
    /// </summary>
    /// <param name="fps">The reading.</param>
    /// <param name="thresholdFps">The frame rate each stream must keep, at most three decimals.</param>
    /// <returns>The rounded reading, on the same side of the threshold as <paramref name="fps"/>.</returns>
    public static double RoundFps(double fps, double thresholdFps)
    {
        var side = Math.Sign(fps.CompareTo(thresholdFps));
        for (var decimals = 1; decimals <= 3; decimals++)
        {
            var rounded = Math.Round(fps, decimals);
            if (Math.Sign(rounded.CompareTo(thresholdFps)) == side)
            {
                return rounded;
            }
        }

        // Within a thousandth of the threshold: round away from it.
        return side < 0 ? Math.Floor(fps * 1000) / 1000 : Math.Ceiling(fps * 1000) / 1000;
    }

    /// <summary>
    /// Formats a stress test frame rate with at least one and at most three decimals: 23.976, 24.0, 23.96.
    /// </summary>
    /// <param name="fps">The frame rate, already rounded with <see cref="RoundFps"/> or to the source's precision.</param>
    /// <returns>The frame rate as text.</returns>
    public static string FormatFps(double fps) => fps.ToString("0.0##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Counts streams for the log: "1 stream", "5 streams".
    /// </summary>
    /// <param name="count">The number of streams.</param>
    /// <returns>The count as text.</returns>
    public static string StreamCount(int count)
        => count == 1 ? "1 stream" : string.Create(CultureInfo.InvariantCulture, $"{count} streams");

    /// <summary>
    /// Describes how a stress test ended, for the log.
    /// </summary>
    /// <param name="result">The finished test.</param>
    /// <returns>How many streams kept up and, when one fell behind, how many were running then.</returns>
    public static string DescribeResult(TranscodeTestStressResult result)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"Result: {result.MaxStableStreams} stream(s) kept up with real time.");
        if (result.EndReason == TranscodeTestEndReason.LimitReached && result.FailedStreamId is int id && result.FailedStreamFps is double fps)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" With {StreamCount(result.StreamsAtEnd)} running, stream {id} fell to {FormatFps(fps)} fps.");
        }

        if (result.Collapsed)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" All streams slowed down sharply at once, rather than one falling a little behind. That usually means the GPU ran out of memory or the driver stalled, not that it was slightly too slow; {StreamCount(result.MaxStableStreams)} is still the safe result.");
        }

        return text;
    }

    /// <summary>
    /// Gets whether the streams collapsed together when the limit was reached: all streams but at most one, and at
    /// least two, are under <see cref="CollapseFraction"/> of the threshold, or their combined frame rate is under
    /// that fraction of the peak. One stream falling behind or stalling while the others keep up is not a collapse.
    /// </summary>
    /// <param name="fps">The running streams' frame rates, <c>null</c> for a stream not measured yet.</param>
    /// <param name="thresholdFps">The frame rate each stream must keep.</param>
    /// <param name="peakCombinedFps">The highest combined frame rate seen in the run.</param>
    /// <returns><c>true</c> for a collapse.</returns>
    public static bool IsCollapse(IReadOnlyList<double?> fps, double thresholdFps, double peakCombinedFps)
    {
        var measured = fps.OfType<double>().ToList();
        if (measured.Count < 2)
        {
            return false;
        }

        var low = measured.Count(f => f < thresholdFps * CollapseFraction);
        if (low >= 2 && low >= measured.Count - 1)
        {
            return true;
        }

        // Only with all streams but at most one measured, so a stream just added does not make the sum look low.
        return fps.Count - measured.Count <= 1 && peakCombinedFps > 0 && measured.Sum() < peakCombinedFps * CollapseFraction;
    }

    /// <summary>
    /// Describes a stream dipping below the threshold, which holds back the next stream until every stream keeps up again.
    /// </summary>
    /// <param name="streamId">The stream that dipped.</param>
    /// <param name="fps">Its frame rate.</param>
    /// <param name="thresholdFps">The frame rate each stream must keep.</param>
    /// <param name="settleSeconds">How long every stream must keep up after a dip before a stream is added.</param>
    /// <param name="nextStreamId">The stream to add next, or null when the test is at its maximum.</param>
    /// <returns>The line for the log.</returns>
    public static string DescribeDip(int streamId, double fps, double thresholdFps, double settleSeconds, int? nextStreamId)
    {
        var reading = FormatFps(RoundFps(fps, thresholdFps));
        return nextStreamId is int next
            ? string.Create(CultureInfo.InvariantCulture, $"Stream {streamId} dipped to {reading} fps; stream {next} waits until every stream has kept up for {settleSeconds:0} s.")
            : string.Create(CultureInfo.InvariantCulture, $"Stream {streamId} dipped to {reading} fps; the test ends once every stream has kept up for {settleSeconds:0} s.");
    }

    /// <summary>
    /// Describes when the stress test adds a stream.
    /// </summary>
    /// <param name="rampSeconds">The least time between two added streams.</param>
    /// <param name="windowSeconds">How long each stream's frame rate is measured over.</param>
    /// <returns>The sentence for the log.</returns>
    public static string DescribeRamp(double rampSeconds, double windowSeconds)
        => string.Create(CultureInfo.InvariantCulture, $"A stream is added at most every {rampSeconds:0} s, once every running stream has kept up for {windowSeconds:0} s.");

    [GeneratedRegex(@"graph [-0-9]+ input from stream 0:0 @ [^\]]+\] .*?pixfmt:([a-z0-9_]+)")]
    private static partial Regex InputFormatRegex();

    // ffmpeg's final statistics: "Input stream #0:0 (video): 96 packets read (123 bytes); 96 frames decoded; ...".
    [GeneratedRegex(@"\b(\d+) frames decoded")]
    private static partial Regex FramesDecodedRegex();

    // ffmpeg's final statistics: "Output stream #0:0 (video): 96 frames encoded; 96 packets muxed (123 bytes);".
    [GeneratedRegex(@"\b(\d+) frames encoded")]
    private static partial Regex FramesEncodedRegex();

    // ffmpeg's progress: "frame=   96 fps=...".
    [GeneratedRegex(@"\bframe=\s*(\d+)")]
    private static partial Regex ProgressFramesRegex();

    // The output stream or encoder an ffmpeg line is about: "[vost#0:0/av1_nvenc @ 0x0]" or "[enc:av1_nvenc @ 0x0]".
    [GeneratedRegex(@"\[(?:vost#\d+:\d+/|enc:)([^ \]]+) @")]
    private static partial Regex StreamTagRegex();

    [GeneratedRegex(@"-c:v \S+_(qsv|cuvid|rkmpp|v4l2m2m)\b")]
    private static partial Regex HardwareDecoderRegex();

    // VideoToolbox tone-maps inside scale_vt when it converts to BT.709.
    [GeneratedRegex(@"(tonemap_[a-z]+|tonemapx|libplacebo)[^,""]*|scale_vt=[^,""]*color_transfer=bt709")]
    private static partial Regex TonemapFilterRegex();
}
