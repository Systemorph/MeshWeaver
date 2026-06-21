using Memex.Client.Voice;
using Plugin.Maui.Audio;
using AudioEncoding = Plugin.Maui.Audio.Encoding;

namespace Memex.Client.Services;

/// <summary>
/// Native microphone capture via Plugin.Maui.Audio, recording 16 kHz mono PCM16 WAV — the format
/// the on-device Whisper transcriber consumes — and decoding it to float samples.
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
