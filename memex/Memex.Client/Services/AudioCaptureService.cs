using Memex.Client.Voice;
using Plugin.Maui.Audio;

namespace Memex.Client.Services;

#if IOS || MACCATALYST
using AVFoundation;

/// <summary>
/// Microphone capture on Apple platforms (iOS / MacCatalyst) via <see cref="AVAudioEngine"/> — a tap on the
/// input bus delivers raw float PCM buffers, which we accumulate and resample to the 16 kHz mono Whisper
/// consumes on stop.
///
/// <para>This replaces Plugin.Maui.Audio's <c>AVAudioRecorder</c> path, which on MacCatalyst recorded
/// nothing AND whose <c>StopAsync()</c> hangs forever waiting on a <c>didFinishRecording</c> delegate that
/// never fires (verified by stack sampling — the await never completes, freezing the UI on "Preparing…").
/// The engine has no such callback: start/stop are synchronous, so the UI can never wedge. Windows/Android
/// keep the Plugin.Maui.Audio path in the #else below.</para>
/// </summary>
public sealed class AudioCaptureService
{
    private const double TargetSampleRate = 16000;

    private readonly object _gate = new();
    private readonly List<float> _samples = new();
    private AVAudioEngine? _engine;
    private double _inputSampleRate;

    // Ctor keeps the IAudioManager parameter so DI registration is identical across platforms.
    public AudioCaptureService(IAudioManager _) { }

    public bool IsRecording { get; private set; }

    public Task StartAsync()
    {
        // Catalyst bridges the iOS-style AVAudioSession; configure + activate it for input.
        var session = AVAudioSession.SharedInstance();
        session.SetCategory(AVAudioSession.CategoryRecord, out _);
        session.SetActive(true, out _);

        lock (_gate) _samples.Clear();

        _engine = new AVAudioEngine();
        var input = _engine.InputNode;
        var format = input.GetBusOutputFormat(0);   // the mic's native format (e.g. 48 kHz float, 1 ch)
        _inputSampleRate = format.SampleRate;

        // The tap fires on a realtime audio thread — copy out fast, append under a short lock.
        input.InstallTapOnBus(0, 4096, format, OnBuffer);
        _engine.Prepare();
        _engine.StartAndReturnError(out _);
        IsRecording = true;
        return Task.CompletedTask;
    }

    private void OnBuffer(AVAudioPcmBuffer buffer, AVAudioTime when)
    {
        var frames = (int)buffer.FrameLength;
        if (frames <= 0) return;
        var chunk = new float[frames];
        unsafe
        {
            // FloatChannelData → array of per-channel float* ; channel 0 is enough (mic input is mono).
            var channels = (float**)buffer.FloatChannelData;
            var ch0 = channels[0];
            for (var i = 0; i < frames; i++) chunk[i] = ch0[i];
        }
        lock (_gate) _samples.AddRange(chunk);
    }

    public Task<float[]> StopAsync()
    {
        if (_engine is null) return Task.FromResult(Array.Empty<float>());

        _engine.InputNode.RemoveTapOnBus(0);
        _engine.Stop();
        _engine.Dispose();
        _engine = null;
        IsRecording = false;

        float[] raw;
        lock (_gate) raw = _samples.ToArray();
        return Task.FromResult(Resample(raw, _inputSampleRate, TargetSampleRate));
    }

    /// <summary>Linear-interpolation resample to 16 kHz mono — ample for speech; Whisper is robust to it.</summary>
    private static float[] Resample(float[] input, double inRate, double outRate)
    {
        if (input.Length == 0 || inRate <= 0 || Math.Abs(inRate - outRate) < 1) return input;
        var ratio = outRate / inRate;
        var outLen = (int)(input.Length * ratio);
        var output = new float[outLen];
        for (var i = 0; i < outLen; i++)
        {
            var src = i / ratio;
            var i0 = (int)src;
            var i1 = Math.Min(i0 + 1, input.Length - 1);
            var frac = src - i0;
            output[i] = (float)(input[i0] * (1 - frac) + input[i1] * frac);
        }
        return output;
    }
}
#else

using AudioEncoding = Plugin.Maui.Audio.Encoding;

/// <summary>
/// Microphone capture via Plugin.Maui.Audio on Windows/Android, recording 16 kHz mono PCM16 WAV — the
/// format the on-device Whisper transcriber consumes — and decoding it to float samples. (Apple platforms
/// use the native <see cref="AVAudioEngine"/> path above.)
/// </summary>
public sealed class AudioCaptureService
{
    private readonly IAudioManager _audioManager;
    private IAudioRecorder? _recorder;

    public AudioCaptureService(IAudioManager audioManager) => _audioManager = audioManager;

    public bool IsRecording => _recorder?.IsRecording ?? false;

    public async Task StartAsync()
    {
        var options = new AudioRecorderOptions
        {
            SampleRate = 16000,
            Channels = ChannelType.Mono,
            BitDepth = BitDepth.Pcm16bit,
            Encoding = AudioEncoding.Wav,
            ThrowIfNotSupported = false,
        };
        _recorder = _audioManager.CreateRecorder(options);
        await _recorder.StartAsync(options);
    }

    public async Task<float[]> StopAsync()
    {
        if (_recorder is null) return [];

        var source = await _recorder.StopAsync();
        _recorder = null;

        await using var stream = source.GetAudioStream();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        ms.Position = 0;
        return WavAudio.ReadPcm16AsMono16k(ms);
    }
}
#endif
