using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace VoicemeeterDelay;

internal sealed class VoicemeeterAudioDelay : IDisposable
{
    private const int CommandStarting = 1;
    private const int CommandEnding = 2;
    private const int CommandChange = 3;
    private const int CommandBufferIn = 10;
    private const int CommandBufferOut = 11;
    private const int CommandBufferMain = 20;

    private readonly VoicemeeterRemote _remote;
    private readonly VoicemeeterRemote.AudioCallback _callback;
    private readonly StreamProcessor _inputProcessor;
    private readonly StreamProcessor _outputProcessor;
    private readonly RouteProcessor _routeProcessor;
    private readonly CallbackMode _registerMode;

    private long _inputCallbackCount;
    private long _outputCallbackCount;
    private long _mainCallbackCount;
    private AudioCallbackSideStats _lastInputStats;
    private AudioCallbackSideStats _lastOutputStats;
    private AudioCallbackSideStats _lastMainStats;
    private bool _started;
    private bool _disposed;

    public VoicemeeterAudioDelay(AppOptions options)
    {
        _remote = VoicemeeterRemote.Load(options.DllPath);
        _callback = Callback;
        _inputProcessor = new StreamProcessor();
        _outputProcessor = new StreamProcessor();
        _routeProcessor = new RouteProcessor();
        _registerMode = options.RegisterMode;
        UpdateOptions(options);
    }

    public CallbackMode RegisterMode => _registerMode;

    public AudioCallbackStats GetCallbackStats()
    {
        return new AudioCallbackStats(
            Interlocked.Read(ref _inputCallbackCount),
            Interlocked.Read(ref _outputCallbackCount),
            Interlocked.Read(ref _mainCallbackCount),
            _lastInputStats,
            _lastOutputStats,
            _lastMainStats);
    }

    public void UpdateOptions(AppOptions options)
    {
        ThrowIfDisposed();

        _inputProcessor.UpdateTargets(options.Targets.Where(static target => target.Mode == CallbackMode.Input).ToArray());
        _outputProcessor.UpdateTargets(options.Targets.Where(static target => target.Mode == CallbackMode.Output).ToArray());
        _routeProcessor.UpdateRoutes(options.Routes.ToArray());
    }

    public void Start()
    {
        ThrowIfDisposed();

        if (_registerMode == CallbackMode.None)
        {
            throw new InvalidOperationException("Choose at least one input or output channel to process.");
        }

        var loginResult = _remote.Login();
        if (loginResult < 0)
        {
            throw new InvalidOperationException($"VBVMR_Login failed with code {loginResult}. Start Voicemeeter and try again.");
        }

        try
        {
            RegisterCallbacks();

            var startResult = _remote.AudioCallbackStart();
            if (startResult < 0)
            {
                throw new InvalidOperationException($"VBVMR_AudioCallbackStart failed with code {startResult}.");
            }
        }
        catch
        {
            _remote.AudioCallbackUnregister();
            _remote.Logout();
            throw;
        }

        _started = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_started)
        {
            _remote.AudioCallbackStop();
            _remote.AudioCallbackUnregister();
            _remote.Logout();
        }

        _remote.Dispose();
        _disposed = true;
    }

    private int Callback(nint user, int command, nint data, int unused)
    {
        try
        {
            switch (command)
            {
                case CommandStarting:
                case CommandChange:
                    ResetForAudioInfo(data);
                    break;

                case CommandEnding:
                    _inputProcessor.Reset();
                    _outputProcessor.Reset();
                    _routeProcessor.Reset();
                    break;

                case CommandBufferIn:
                case CommandBufferOut:
                case CommandBufferMain:
                    ProcessAudioBuffer(data, command);
                    break;
            }
        }
        catch
        {
            // The Voicemeeter callback is real-time audio code. Never throw back into the host.
        }

        return 0;
    }

    private void RegisterCallbacks()
    {
        var registerResult = _remote.AudioCallbackRegister((int)_registerMode, _callback, "VoicemeeterDelay");
        if (registerResult != 0)
        {
            throw new InvalidOperationException($"VBVMR_AudioCallbackRegister failed with code {registerResult}. Another callback app may already be registered.");
        }
    }

    private unsafe void ResetForAudioInfo(nint audioInfoPointer)
    {
        if (audioInfoPointer == 0)
        {
            return;
        }

        var info = (int*)audioInfoPointer;
        var sampleRate = info[0];

        _inputProcessor.ResetSampleRate(sampleRate);
        _outputProcessor.ResetSampleRate(sampleRate);
        _routeProcessor.ResetSampleRate(sampleRate);
    }

    private unsafe void ProcessAudioBuffer(nint audioBufferPointer, int command)
    {
        if (audioBufferPointer == 0)
        {
            return;
        }

        var buffer = (byte*)audioBufferPointer;
        var sampleRate = Unsafe.ReadUnaligned<int>(buffer);
        var sampleCount = Unsafe.ReadUnaligned<int>(buffer + 4);
        var inputChannels = Unsafe.ReadUnaligned<int>(buffer + 8);
        var outputChannels = Unsafe.ReadUnaligned<int>(buffer + 12);

        RecordCallback(command, buffer, sampleRate, sampleCount, inputChannels, outputChannels);

        if (sampleRate <= 0 || sampleCount <= 0)
        {
            return;
        }

        var boundedInputChannels = Math.Clamp(inputChannels, 0, 128);
        var boundedOutputChannels = Math.Clamp(outputChannels, 0, 128);

        var processor = command switch
        {
            CommandBufferIn => _inputProcessor,
            CommandBufferOut => _outputProcessor,
            _ => null
        };

        if (processor is null)
        {
            return;
        }

        // Baseline: input insert must be driven by audiobuffer_nbi. Using nbo here made
        // the input callback unstable; this nbi path is the low-latency working behavior.
        var channelCount = command == CommandBufferIn
            ? boundedInputChannels
            : boundedOutputChannels;
        if (channelCount <= 0)
        {
            return;
        }

        processor.Ensure(sampleRate, boundedInputChannels, channelCount);
        if (command is CommandBufferIn or CommandBufferOut)
        {
            _routeProcessor.Ensure(sampleRate);
        }

        var readBuffers = (nint*)(buffer + 16);
        var writeBuffers = (nint*)(buffer + 16 + (IntPtr.Size * 128));

        for (var channel = 0; channel < channelCount; channel++)
        {
            var input = channel < boundedInputChannels ? (float*)readBuffers[channel] : null;
            var output = (float*)writeBuffers[channel];

            var muteNormal = command == CommandBufferIn
                && input != null
                && _routeProcessor.CaptureInput(channel, input, sampleCount);

            if (output == null)
            {
                continue;
            }

            var channelProcessor = processor.GetDelay(channel);
            if (channelProcessor is null || input == null)
            {
                if (input != null)
                {
                    Copy(input, output, sampleCount);
                }

                if (muteNormal)
                {
                    Clear(output, sampleCount);
                }

                if (command == CommandBufferOut)
                {
                    _routeProcessor.MixOutput(channel, output, sampleCount);
                }

                continue;
            }

            for (var sample = 0; sample < sampleCount; sample++)
            {
                output[sample] = channelProcessor.Process(input[sample]);
            }

            if (muteNormal)
            {
                Clear(output, sampleCount);
            }

            if (command == CommandBufferOut)
            {
                _routeProcessor.MixOutput(channel, output, sampleCount);
            }
        }
    }

    private static unsafe void Copy(float* input, float* output, int sampleCount)
    {
        if (input == output)
        {
            return;
        }

        Buffer.MemoryCopy(input, output, sampleCount * sizeof(float), sampleCount * sizeof(float));
    }

    private static unsafe void Clear(float* output, int sampleCount)
    {
        new Span<float>(output, sampleCount).Clear();
    }

    private unsafe void RecordCallback(
        int command,
        byte* buffer,
        int sampleRate,
        int sampleCount,
        int inputChannels,
        int outputChannels)
    {
        if (sampleRate <= 0 || sampleCount <= 0)
        {
            return;
        }

        var inspectChannels = command switch
        {
            CommandBufferIn => inputChannels,
            CommandBufferOut => outputChannels,
            CommandBufferMain => Math.Max(inputChannels, outputChannels),
            _ => 0
        };

        inspectChannels = Math.Clamp(inspectChannels, 0, 128);
        var readBuffers = (nint*)(buffer + 16);
        var writeBuffers = (nint*)(buffer + 16 + (IntPtr.Size * 128));
        var readableBuffers = 0;
        var writableBuffers = 0;

        for (var channel = 0; channel < inspectChannels; channel++)
        {
            if (readBuffers[channel] != 0)
            {
                readableBuffers++;
            }

            if (writeBuffers[channel] != 0)
            {
                writableBuffers++;
            }
        }

        var stats = new AudioCallbackSideStats(
            sampleRate,
            sampleCount,
            inputChannels,
            outputChannels,
            readableBuffers,
            writableBuffers);

        switch (command)
        {
            case CommandBufferIn:
                _lastInputStats = stats;
                Interlocked.Increment(ref _inputCallbackCount);
                break;

            case CommandBufferOut:
                _lastOutputStats = stats;
                Interlocked.Increment(ref _outputCallbackCount);
                break;

            case CommandBufferMain:
                _lastMainStats = stats;
                Interlocked.Increment(ref _mainCallbackCount);
                break;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VoicemeeterAudioDelay));
        }
    }

    private sealed class StreamProcessor
    {
        private readonly object _sync = new();
        private DelayTarget[] _targets = [];
        private ChannelProcessor?[] _delays = new ChannelProcessor?[128];
        private int _sampleRate;
        private int _inputChannels;
        private int _outputChannels;
        private int _targetVersion;
        private int _builtTargetVersion = -1;

        public void UpdateTargets(DelayTarget[] targets)
        {
            lock (_sync)
            {
                _targets = targets;
                _targetVersion++;

                if (_sampleRate > 0 && _inputChannels > 0 && _outputChannels > 0)
                {
                    RebuildUnderLock(_sampleRate, _inputChannels, _outputChannels);
                }
                else
                {
                    _delays = new ChannelProcessor?[128];
                    _builtTargetVersion = _targetVersion;
                }
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                _delays = new ChannelProcessor?[128];
                _sampleRate = 0;
                _inputChannels = 0;
                _outputChannels = 0;
                _builtTargetVersion = -1;
            }
        }

        public void ResetSampleRate(int sampleRate)
        {
            lock (_sync)
            {
                if (_sampleRate == sampleRate)
                {
                    return;
                }

                _delays = new ChannelProcessor?[128];
                _sampleRate = sampleRate;
                _inputChannels = 0;
                _outputChannels = 0;
                _builtTargetVersion = -1;
            }
        }

        public void Ensure(int sampleRate, int inputChannels, int outputChannels)
        {
            var targetVersion = _targetVersion;
            if (_sampleRate == sampleRate
                && _inputChannels == inputChannels
                && _outputChannels == outputChannels
                && _builtTargetVersion == targetVersion)
            {
                return;
            }

            lock (_sync)
            {
                if (_sampleRate == sampleRate
                    && _inputChannels == inputChannels
                    && _outputChannels == outputChannels
                    && _builtTargetVersion == _targetVersion)
                {
                    return;
                }

                RebuildUnderLock(sampleRate, inputChannels, outputChannels);
            }
        }

        public ChannelProcessor? GetDelay(int zeroBasedChannel)
        {
            var delays = _delays;
            return zeroBasedChannel >= 0 && zeroBasedChannel < delays.Length
                ? delays[zeroBasedChannel]
                : null;
        }

        private void RebuildUnderLock(int sampleRate, int inputChannels, int outputChannels)
        {
            var previousDelays = _delays;
            var delays = new ChannelProcessor?[128];
            var availableChannels = Math.Min(Math.Min(inputChannels, outputChannels), delays.Length);

            foreach (var target in _targets)
            {
                var delaySamples = DelayLine.MillisecondsToSamples(target.DelayMilliseconds, sampleRate);
                if (delaySamples <= 0 && IsUnityGain(target.Gain))
                {
                    continue;
                }

                for (var channel = 0; channel < availableChannels; channel++)
                {
                    if (target.Channels.IncludesZeroBased(channel))
                    {
                        var previousDelay = previousDelays[channel];
                        if (previousDelay?.DelaySamples == delaySamples)
                        {
                            previousDelay.UpdateGain(target.Gain);
                            delays[channel] = previousDelay;
                        }
                        else
                        {
                            delays[channel] = new ChannelProcessor(delaySamples, target.Gain);
                        }
                    }
                }
            }

            _delays = delays;
            _sampleRate = sampleRate;
            _inputChannels = inputChannels;
            _outputChannels = outputChannels;
            _builtTargetVersion = _targetVersion;
        }

        private static bool IsUnityGain(double gain)
        {
            return NearlyEqual(gain, 1.0);
        }

        private static bool NearlyEqual(double left, double right)
        {
            return Math.Abs(left - right) < 0.0001;
        }
    }

    private sealed class RouteProcessor
    {
        private readonly object _sync = new();
        private AudioRoute[] _routes = [];
        private RouteLine[] _lines = [];
        private int _sampleRate;
        private int _routeVersion;
        private int _builtRouteVersion = -1;

        public void UpdateRoutes(AudioRoute[] routes)
        {
            lock (_sync)
            {
                _routes = routes;
                _routeVersion++;

                if (_sampleRate > 0)
                {
                    RebuildUnderLock(_sampleRate);
                }
                else
                {
                    _lines = [];
                    _builtRouteVersion = _routeVersion;
                }
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                _lines = [];
                _sampleRate = 0;
                _builtRouteVersion = -1;
            }
        }

        public void ResetSampleRate(int sampleRate)
        {
            lock (_sync)
            {
                if (_sampleRate == sampleRate)
                {
                    return;
                }

                _lines = [];
                _sampleRate = sampleRate;
                _builtRouteVersion = -1;
            }
        }

        public void Ensure(int sampleRate)
        {
            if (_sampleRate == sampleRate && _builtRouteVersion == _routeVersion)
            {
                return;
            }

            lock (_sync)
            {
                if (_sampleRate == sampleRate && _builtRouteVersion == _routeVersion)
                {
                    return;
                }

                RebuildUnderLock(sampleRate);
            }
        }

        public unsafe bool CaptureInput(int sourceChannel, float* input, int sampleCount)
        {
            var muteNormal = false;
            foreach (var line in _lines)
            {
                if (line.SourceChannel != sourceChannel)
                {
                    continue;
                }

                muteNormal |= line.MuteSource;
                line.Capture(input, sampleCount);
            }

            return muteNormal;
        }

        public unsafe void MixOutput(int destinationChannel, float* output, int sampleCount)
        {
            foreach (var line in _lines)
            {
                if (line.DestinationChannel == destinationChannel)
                {
                    line.MixTo(output, sampleCount);
                }
            }
        }

        private void RebuildUnderLock(int sampleRate)
        {
            var lines = new List<RouteLine>(_routes.Length);
            foreach (var route in _routes)
            {
                var delaySamples = DelayLine.MillisecondsToSamples(route.DelayMilliseconds, sampleRate);
                lines.Add(new RouteLine(route, delaySamples, sampleRate));
            }

            _lines = lines.ToArray();
            _sampleRate = sampleRate;
            _builtRouteVersion = _routeVersion;
        }
    }

    private sealed class RouteLine
    {
        private readonly ChannelProcessor _processor;
        private readonly FloatRingBuffer _buffer;
        private float[] _scratch = [];

        public RouteLine(AudioRoute route, int delaySamples, int sampleRate)
        {
            SourceChannel = route.SourceChannel;
            DestinationChannel = route.DestinationChannel;
            MuteSource = route.MuteSource;
            _processor = new ChannelProcessor(delaySamples, route.Gain);
            _buffer = new FloatRingBuffer(Math.Max(sampleRate, 4096));
        }

        public int SourceChannel { get; }

        public int DestinationChannel { get; }

        public bool MuteSource { get; }

        public unsafe void Capture(float* input, int sampleCount)
        {
            if (_scratch.Length < sampleCount)
            {
                _scratch = new float[sampleCount];
            }

            for (var sample = 0; sample < sampleCount; sample++)
            {
                _scratch[sample] = _processor.Process(input[sample]);
            }

            _buffer.Enqueue(_scratch.AsSpan(0, sampleCount));
        }

        public unsafe void MixTo(float* output, int sampleCount)
        {
            _buffer.MixTo(output, sampleCount);
        }
    }

    private sealed class FloatRingBuffer
    {
        private readonly object _sync = new();
        private readonly float[] _buffer;
        private int _readIndex;
        private int _writeIndex;
        private int _count;

        public FloatRingBuffer(int capacity)
        {
            _buffer = new float[Math.Max(capacity, 1)];
        }

        public void Enqueue(ReadOnlySpan<float> samples)
        {
            lock (_sync)
            {
                foreach (var sample in samples)
                {
                    if (_count == _buffer.Length)
                    {
                        _readIndex = Wrap(_readIndex + 1);
                        _count--;
                    }

                    _buffer[_writeIndex] = sample;
                    _writeIndex = Wrap(_writeIndex + 1);
                    _count++;
                }
            }
        }

        public unsafe void MixTo(float* output, int sampleCount)
        {
            lock (_sync)
            {
                for (var sample = 0; sample < sampleCount; sample++)
                {
                    if (_count == 0)
                    {
                        return;
                    }

                    output[sample] += _buffer[_readIndex];
                    _readIndex = Wrap(_readIndex + 1);
                    _count--;
                }
            }
        }

        private int Wrap(int index)
        {
            return index == _buffer.Length ? 0 : index;
        }
    }

    private sealed class ChannelProcessor
    {
        private readonly float[] _buffer;
        private int _gainBits;
        private int _position;
        private int _primedSamples;

        public ChannelProcessor(int delaySamples, double gain)
        {
            _buffer = new float[delaySamples];
            UpdateGain(gain);
        }

        public int DelaySamples => _buffer.Length;

        public double Gain => GainValue;

        public void UpdateGain(double gain)
        {
            Volatile.Write(ref _gainBits, BitConverter.SingleToInt32Bits((float)gain));
        }

        public float Process(float input)
        {
            var gain = GainValue;
            if (_buffer.Length == 0)
            {
                return input * gain;
            }

            if (_primedSamples < _buffer.Length)
            {
                _buffer[_position] = input;
                Advance();
                _primedSamples++;
                return input * gain;
            }

            var delayed = _buffer[_position];
            _buffer[_position] = input;
            Advance();

            return delayed * gain;
        }

        private float GainValue => BitConverter.Int32BitsToSingle(Volatile.Read(ref _gainBits));

        private void Advance()
        {
            _position++;
            if (_position == _buffer.Length)
            {
                _position = 0;
            }
        }
    }
}

internal readonly record struct AudioCallbackStats(
    long InputCallbacks,
    long OutputCallbacks,
    long MainCallbacks,
    AudioCallbackSideStats Input,
    AudioCallbackSideStats Output,
    AudioCallbackSideStats Main);

internal readonly record struct AudioCallbackSideStats(
    int SampleRate,
    int SampleCount,
    int InputChannels,
    int OutputChannels,
    int ReadableBuffers,
    int WritableBuffers);
