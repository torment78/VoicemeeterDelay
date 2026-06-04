using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoicemeeterDelay;

internal sealed record RoundTripMeasurementResult(
    string PingInputName,
    string ReturnOutputName,
    bool Detected,
    double RoundTripMilliseconds,
    double DetectionLevel,
    string Message,
    int BestCaptureChannelIndex = -1,
    double BestCaptureChannelLevel = 0);

internal static class RoundTripMeasurementEngine
{
    private static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid AudioRenderClientId = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private static readonly Guid AudioCaptureClientId = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");

    private const long BufferDurationHundredNanoseconds = 1_000_000;
    private const double PreRollSeconds = 0.08;
    private const double PingSeconds = 0.025;
    private const double PingFrequencyHz = 1800.0;
    private const double DetectionThreshold = 0.05;
    private static readonly SemaphoreSlim MeasurementGate = new(1, 1);
    public const double DefaultTimeoutSeconds = 10.0;

    public static async Task<RoundTripMeasurementResult> MeasureAsync(
        AudioEndpointChoice pingInput,
        AudioEndpointChoice returnOutput,
        int pingChannelIndex,
        int returnChannelIndex,
        double timeoutSeconds,
        CancellationToken cancellationToken)
    {
        await MeasurementGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunOnStaThread(
                () => Measure(
                pingInput,
                returnOutput,
                pingChannelIndex,
                returnChannelIndex,
                Math.Clamp(timeoutSeconds, 1.0, 30.0),
                cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            MeasurementGate.Release();
        }
    }

    public static int GetEndpointChannelCount(AudioEndpointChoice endpoint)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? audioClient = null;
        nint formatPointer = 0;

        try
        {
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(endpoint.Id, out device));
            audioClient = ActivateAudioClient(device);
            Marshal.ThrowExceptionForHR(audioClient.GetMixFormat(out formatPointer));
            return WaveFormatInfo.FromPointer(formatPointer).Channels;
        }
        finally
        {
            if (formatPointer != 0)
            {
                Marshal.FreeCoTaskMem(formatPointer);
            }

            ReleaseComObject(audioClient);
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
        }
    }

    private static RoundTripMeasurementResult Measure(
        AudioEndpointChoice pingInput,
        AudioEndpointChoice returnOutput,
        int pingChannelIndex,
        int returnChannelIndex,
        double timeoutSeconds,
        CancellationToken cancellationToken)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? renderDevice = null;
        IMMDevice? captureDevice = null;
        IAudioClient? renderClient = null;
        IAudioClient? captureClient = null;
        IAudioRenderClient? renderService = null;
        IAudioCaptureClient? captureService = null;
        nint renderFormatPointer = 0;
        nint captureFormatPointer = 0;
        var renderStarted = false;
        var captureStarted = false;

        try
        {
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(pingInput.Id, out renderDevice));
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(returnOutput.Id, out captureDevice));

            renderClient = ActivateAudioClient(renderDevice);
            captureClient = ActivateAudioClient(captureDevice);

            Marshal.ThrowExceptionForHR(renderClient.GetMixFormat(out renderFormatPointer));
            Marshal.ThrowExceptionForHR(captureClient.GetMixFormat(out captureFormatPointer));
            var renderFormat = WaveFormatInfo.FromPointer(renderFormatPointer);
            var captureFormat = WaveFormatInfo.FromPointer(captureFormatPointer);
            ValidateChannelIndex(pingChannelIndex, renderFormat, "ping input", pingInput.Name);
            ValidateChannelIndex(returnChannelIndex, captureFormat, "return output", returnOutput.Name);

            var sessionId = Guid.Empty;
            Marshal.ThrowExceptionForHR(renderClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.None,
                BufferDurationHundredNanoseconds,
                0,
                renderFormatPointer,
                ref sessionId));

            sessionId = Guid.Empty;
            Marshal.ThrowExceptionForHR(captureClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.None,
                BufferDurationHundredNanoseconds,
                0,
                captureFormatPointer,
                ref sessionId));

            Marshal.ThrowExceptionForHR(renderClient.GetBufferSize(out var renderBufferFrames));
            var renderServiceId = AudioRenderClientId;
            Marshal.ThrowExceptionForHR(renderClient.GetService(ref renderServiceId, out var renderServiceObject));
            renderService = (IAudioRenderClient)renderServiceObject;

            var captureServiceId = AudioCaptureClientId;
            Marshal.ThrowExceptionForHR(captureClient.GetService(ref captureServiceId, out var captureServiceObject));
            captureService = (IAudioCaptureClient)captureServiceObject;

            long renderFramesWritten = 0;
            var captureFramesRead = 0L;
            FillRenderBuffer(renderClient, renderService, renderFormat, renderBufferFrames, pingChannelIndex, ref renderFramesWritten);

            var stopwatchFrequency = Stopwatch.Frequency;
            Marshal.ThrowExceptionForHR(captureClient.Start());
            captureStarted = true;
            var captureStartTimestamp = Stopwatch.GetTimestamp();
            Marshal.ThrowExceptionForHR(renderClient.Start());
            renderStarted = true;
            var renderStartTimestamp = Stopwatch.GetTimestamp();
            var captureStartSecondsFromRenderStart = (double)(captureStartTimestamp - renderStartTimestamp) / stopwatchFrequency;
            var ignoreCaptureFramesBefore = checked((long)Math.Max(
                0,
                (PreRollSeconds - captureStartSecondsFromRenderStart) * captureFormat.SampleRate));
            var timeoutTimestamp = renderStartTimestamp + checked((long)(timeoutSeconds * stopwatchFrequency));
            var bestLevel = 0.0;
            var bestCaptureChannelIndex = -1;
            var bestCaptureChannelLevel = 0.0;

            try
            {
                while (Stopwatch.GetTimestamp() < timeoutTimestamp)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FillRenderBuffer(renderClient, renderService, renderFormat, renderBufferFrames, pingChannelIndex, ref renderFramesWritten);

                    if (TryReadCapture(
                        captureService,
                        captureFormat,
                        returnChannelIndex,
                        ref captureFramesRead,
                        ignoreCaptureFramesBefore,
                        out var detectedFrame,
                        out var detectedLevel,
                        ref bestLevel,
                        ref bestCaptureChannelIndex,
                        ref bestCaptureChannelLevel))
                    {
                        var detectedSecondsFromRenderStart =
                            captureStartSecondsFromRenderStart
                            + ((double)detectedFrame / captureFormat.SampleRate);
                        var roundTripMilliseconds = Math.Max(0, (detectedSecondsFromRenderStart - PreRollSeconds) * 1000.0);
                        return new RoundTripMeasurementResult(
                            pingInput.Name,
                            returnOutput.Name,
                            Detected: true,
                            roundTripMilliseconds,
                            detectedLevel,
                            "Ping return detected.",
                            BestCaptureChannelIndex: returnChannelIndex,
                            BestCaptureChannelLevel: detectedLevel);
                    }

                    Thread.Sleep(2);
                }
            }
            finally
            {
                if (renderStarted)
                {
                    TryStop(renderClient);
                }

                if (captureStarted)
                {
                    TryStop(captureClient);
                }
            }

            return new RoundTripMeasurementResult(
                pingInput.Name,
                returnOutput.Name,
                Detected: false,
                RoundTripMilliseconds: 0,
                DetectionLevel: bestLevel,
                Message: BuildNoReturnMessage(bestLevel, bestCaptureChannelIndex, bestCaptureChannelLevel),
                BestCaptureChannelIndex: bestCaptureChannelIndex,
                BestCaptureChannelLevel: bestCaptureChannelLevel);
        }
        finally
        {
            if (renderFormatPointer != 0)
            {
                Marshal.FreeCoTaskMem(renderFormatPointer);
            }

            if (captureFormatPointer != 0)
            {
                Marshal.FreeCoTaskMem(captureFormatPointer);
            }

            ReleaseComObject(captureService);
            ReleaseComObject(renderService);
            ReleaseComObject(captureClient);
            ReleaseComObject(renderClient);
            ReleaseComObject(captureDevice);
            ReleaseComObject(renderDevice);
            ReleaseComObject(enumerator);
        }
    }

    private static IAudioClient ActivateAudioClient(IMMDevice device)
    {
        var audioClientId = AudioClientId;
        Marshal.ThrowExceptionForHR(device.Activate(
            ref audioClientId,
            ClsCtx.InprocServer,
            0,
            out var clientObject));
        return (IAudioClient)clientObject;
    }

    private static Task<T> RunOnStaThread<T>(Func<T> work, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(work());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "VoicemeeterDelay round trip measurement"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void TryStop(IAudioClient? audioClient)
    {
        try
        {
            audioClient?.Stop();
        }
        catch
        {
            // Preserve the original measurement result/error.
        }
    }

    private static void ValidateChannelIndex(
        int channelIndex,
        WaveFormatInfo format,
        string endpointRole,
        string endpointName)
    {
        if (channelIndex >= 0 && channelIndex < format.Channels)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{endpointRole} '{endpointName}' exposes {format.Channels} channel(s), so channel {channelIndex + 1} cannot be used.");
    }

    private static string BuildNoReturnMessage(
        double selectedChannelLevel,
        int bestCaptureChannelIndex,
        double bestCaptureChannelLevel)
    {
        var message = $"No ping return detected on selected return channel. Selected channel highest level was {selectedChannelLevel:0.000}; threshold is {DetectionThreshold:0.000}.";
        if (bestCaptureChannelIndex < 0)
        {
            return message + " No signal was captured on the selected return device.";
        }

        return message
            + $" Strongest captured channel was Ch {bestCaptureChannelIndex + 1} at {bestCaptureChannelLevel:0.000}.";
    }

    private static void FillRenderBuffer(
        IAudioClient audioClient,
        IAudioRenderClient renderClient,
        WaveFormatInfo format,
        uint bufferFrames,
        int pingChannelIndex,
        ref long framesWritten)
    {
        Marshal.ThrowExceptionForHR(audioClient.GetCurrentPadding(out var paddingFrames));
        if (paddingFrames >= bufferFrames)
        {
            return;
        }

        var framesAvailable = bufferFrames - paddingFrames;
        if (framesAvailable == 0)
        {
            return;
        }

        Marshal.ThrowExceptionForHR(renderClient.GetBuffer(framesAvailable, out var buffer));
        try
        {
            WritePingFrames(buffer, format, framesAvailable, framesWritten, pingChannelIndex);
            framesWritten += framesAvailable;
        }
        finally
        {
            Marshal.ThrowExceptionForHR(renderClient.ReleaseBuffer(framesAvailable, AudioClientBufferFlags.None));
        }
    }

    private static unsafe void WritePingFrames(
        nint buffer,
        WaveFormatInfo format,
        uint frameCount,
        long streamFrameOffset,
        int pingChannelIndex)
    {
        if (buffer == 0)
        {
            return;
        }

        for (var frame = 0; frame < frameCount; frame++)
        {
            var sampleFrame = streamFrameOffset + frame;
            var seconds = (double)sampleFrame / format.SampleRate;
            var value = 0.0f;
            if (seconds >= PreRollSeconds && seconds < PreRollSeconds + PingSeconds)
            {
                var pingPosition = seconds - PreRollSeconds;
                var envelope = Math.Sin(Math.PI * pingPosition / PingSeconds);
                value = (float)(0.85 * envelope * Math.Sin(2.0 * Math.PI * PingFrequencyHz * pingPosition));
            }

            for (var channel = 0; channel < format.Channels; channel++)
            {
                var sampleIndex = (frame * format.Channels) + channel;
                var channelValue = channel == pingChannelIndex ? value : 0.0f;
                switch (format.SampleFormat)
                {
                    case AudioSampleFormat.Float32:
                        ((float*)buffer)[sampleIndex] = channelValue;
                        break;

                    case AudioSampleFormat.Int16:
                        ((short*)buffer)[sampleIndex] = (short)Math.Clamp(channelValue * short.MaxValue, short.MinValue, short.MaxValue);
                        break;
                }
            }
        }
    }

    private static bool TryReadCapture(
        IAudioCaptureClient captureClient,
        WaveFormatInfo format,
        int returnChannelIndex,
        ref long framesRead,
        long ignoreFramesBefore,
        out long detectedFrame,
        out double detectedLevel,
        ref double bestLevel,
        ref int bestCaptureChannelIndex,
        ref double bestCaptureChannelLevel)
    {
        detectedFrame = 0;
        detectedLevel = 0;

        Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out var packetFrames));
        while (packetFrames > 0)
        {
            Marshal.ThrowExceptionForHR(captureClient.GetBuffer(
                out var buffer,
                out var framesAvailable,
                out var flags,
                out _,
                out _));
            try
            {
                if (!flags.HasFlag(AudioClientBufferFlags.Silent)
                    && TryDetectPing(
                        buffer,
                        format,
                        framesAvailable,
                        returnChannelIndex,
                        framesRead,
                        ignoreFramesBefore,
                        out detectedFrame,
                        out detectedLevel,
                        ref bestLevel,
                        ref bestCaptureChannelIndex,
                        ref bestCaptureChannelLevel))
                {
                    return true;
                }

                framesRead += framesAvailable;
            }
            finally
            {
                Marshal.ThrowExceptionForHR(captureClient.ReleaseBuffer(framesAvailable));
            }

            Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out packetFrames));
        }

        return false;
    }

    private static unsafe bool TryDetectPing(
        nint buffer,
        WaveFormatInfo format,
        uint frameCount,
        int returnChannelIndex,
        long framesRead,
        long ignoreFramesBefore,
        out long detectedFrame,
        out double detectedLevel,
        ref double bestLevel,
        ref int bestCaptureChannelIndex,
        ref double bestCaptureChannelLevel)
    {
        detectedFrame = 0;
        detectedLevel = 0;
        if (buffer == 0)
        {
            return false;
        }

        for (var frame = 0; frame < frameCount; frame++)
        {
            var selectedLevel = 0.0;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var sampleIndex = (frame * format.Channels) + channel;
                var value = format.SampleFormat switch
                {
                    AudioSampleFormat.Float32 => ((float*)buffer)[sampleIndex],
                    AudioSampleFormat.Int16 => ((short*)buffer)[sampleIndex] / (double)short.MaxValue,
                    _ => 0
                };
                var channelLevel = Math.Abs(value);
                if (channelLevel > bestCaptureChannelLevel)
                {
                    bestCaptureChannelLevel = channelLevel;
                    bestCaptureChannelIndex = channel;
                }

                if (channel == returnChannelIndex)
                {
                    selectedLevel = channelLevel;
                }
            }

            bestLevel = Math.Max(bestLevel, selectedLevel);
            if (framesRead + frame >= ignoreFramesBefore && selectedLevel >= DetectionThreshold)
            {
                detectedFrame = framesRead + frame;
                detectedLevel = selectedLevel;
                return true;
            }
        }

        return false;
    }

    private static void ReleaseComObject(object? comObject)
    {
        if (comObject is not null)
        {
            try
            {
                Marshal.ReleaseComObject(comObject);
            }
            catch
            {
                // Releasing a COM object must not hide the measurement result/error.
            }
        }
    }

    private enum AudioSampleFormat
    {
        Float32,
        Int16
    }

    private sealed record WaveFormatInfo(int SampleRate, int Channels, AudioSampleFormat SampleFormat)
    {
        private const ushort WaveFormatPcm = 1;
        private const ushort WaveFormatIeeeFloat = 3;
        private const ushort WaveFormatExtensible = 0xFFFE;

        public static WaveFormatInfo FromPointer(nint pointer)
        {
            var format = Marshal.PtrToStructure<WaveFormatEx>(pointer);
            var sampleFormat = format.FormatTag switch
            {
                WaveFormatIeeeFloat when format.BitsPerSample == 32 => AudioSampleFormat.Float32,
                WaveFormatPcm when format.BitsPerSample == 16 => AudioSampleFormat.Int16,
                WaveFormatExtensible => ReadExtensibleFormat(pointer, format),
                _ => throw new InvalidOperationException($"Unsupported audio format: tag {format.FormatTag}, {format.BitsPerSample} bit.")
            };

            return new WaveFormatInfo(checked((int)format.SamplesPerSecond), format.Channels, sampleFormat);
        }

        private static AudioSampleFormat ReadExtensibleFormat(nint pointer, WaveFormatEx format)
        {
            var extensible = Marshal.PtrToStructure<WaveFormatExtensible>(pointer);
            if (format.BitsPerSample == 32 && extensible.SubFormat == IeeeFloatSubFormat)
            {
                return AudioSampleFormat.Float32;
            }

            if (format.BitsPerSample == 16 && extensible.SubFormat == PcmSubFormat)
            {
                return AudioSampleFormat.Int16;
            }

            throw new InvalidOperationException($"Unsupported extensible audio format: {format.BitsPerSample} bit, {extensible.SubFormat}.");
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private readonly struct WaveFormatEx
    {
        public readonly ushort FormatTag;
        public readonly ushort Channels;
        public readonly uint SamplesPerSecond;
        public readonly uint AverageBytesPerSecond;
        public readonly ushort BlockAlign;
        public readonly ushort BitsPerSample;
        public readonly ushort Size;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private readonly struct WaveFormatExtensible
    {
        public readonly WaveFormatEx Format;
        public readonly ushort ValidBitsPerSample;
        public readonly uint ChannelMask;
        public readonly Guid SubFormat;
    }

    private enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2
    }

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [Flags]
    private enum DeviceState
    {
        Active = 1
    }

    [Flags]
    private enum ClsCtx
    {
        InprocServer = 1
    }

    private enum AudioClientShareMode
    {
        Shared = 0,
        Exclusive = 1
    }

    [Flags]
    private enum AudioClientStreamFlags
    {
        None = 0
    }

    [Flags]
    private enum AudioClientBufferFlags
    {
        None = 0,
        DataDiscontinuity = 1,
        Silent = 2,
        TimestampError = 4
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumerator;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, ClsCtx classContext, nint activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(
            AudioClientShareMode shareMode,
            AudioClientStreamFlags streamFlags,
            long bufferDuration,
            long periodicity,
            nint format,
            ref Guid audioSessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint bufferFrameCount);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint paddingFrameCount);

        [PreserveSig]
        int IsFormatSupported(AudioClientShareMode shareMode, nint format, out nint closestMatch);

        [PreserveSig]
        int GetMixFormat(out nint deviceFormat);

        [PreserveSig]
        int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(nint eventHandle);

        [PreserveSig]
        int GetService(ref Guid serviceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig]
        int GetBuffer(uint framesRequested, out nint data);

        [PreserveSig]
        int ReleaseBuffer(uint framesWritten, AudioClientBufferFlags flags);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out nint data, out uint framesAvailable, out AudioClientBufferFlags flags, out long devicePosition, out long qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(uint framesRead);

        [PreserveSig]
        int GetNextPacketSize(out uint framesInNextPacket);
    }
}
