using System.Reactive.Linq;
using Memex.Client.Services;
using Memex.Client.Voice;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Media;

namespace Memex.Client.Pages;

/// <summary>
/// Native on-device voice: record → Whisper transcription (on the GPU where available) → if Apple
/// Intelligence is available, an on-device reply is generated and spoken back. The native MAUI replacement
/// for the old Blazor Voice page; uses the same services (<see cref="VoiceService"/>,
/// <see cref="AudioCaptureService"/>, <see cref="IOnDeviceChat"/>).
/// </summary>
public sealed class VoicePage : ContentPage
{
    private readonly AudioCaptureService _capture;
    private readonly VoiceService _voice;
    private readonly IOnDeviceChat _ai;

    private readonly Button _record = new() { Text = "● Record", FontSize = 18 };
    private readonly Label _status = new() { Text = "Tap record and speak.", TextColor = Colors.Gray };
    private readonly Label _engine = new() { IsVisible = false, FontSize = 12, TextColor = Colors.Green };
    private readonly Label _transcript = new();
    private readonly Label _reply = new() { IsVisible = false, TextColor = Colors.RoyalBlue };
    private IDisposable? _sub;

    public VoicePage(AudioCaptureService capture, VoiceService voice, IOnDeviceChat ai)
    {
        _capture = capture;
        _voice = voice;
        _ai = ai;
        Title = "Voice";
        _record.Clicked += OnRecordClicked;

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 20,
                Spacing = 14,
                Children =
                {
                    new Label { Text = "🎙️ Voice", FontSize = 24, FontAttributes = FontAttributes.Bold },
                    new Label { Text = "Speech is transcribed on this device (Whisper).", TextColor = Colors.Gray },
                    _record,
                    _status,
                    _engine,
                    new Label { Text = "You said", FontAttributes = FontAttributes.Bold },
                    _transcript,
                    _reply,
                },
            },
        };
    }

    private async void OnRecordClicked(object? sender, EventArgs e)
    {
        if (!_capture.IsRecording)
        {
            await _capture.StartAsync();
            _record.Text = "■ Stop & transcribe";
            _status.Text = "Recording… speak now.";
            return;
        }

        _record.Text = "● Record";
        _status.Text = "Preparing…";
        var samples = await _capture.StopAsync();
        if (samples.Length == 0)
        {
            _status.Text = "No audio captured.";
            return;
        }

        _transcript.Text = "";
        var segments = new List<string>();
        var language = FeatureFlags.IsSwissGermanEnabled ? WhisperTranscriber.AutoPreferGermanEnglish : "en";

        // Real feedback: the first run copies the bundled model out of the app package and loads it (a few
        // seconds) BEFORE any audio is decoded — without this the UI would sit on one frozen line and look
        // hung. status = coarse stage ("Model ready (bundled).", …); progress = 0–100 during transcription.
        // Progress<T> marshals callbacks to the captured (UI) SynchronizationContext, so no MainThread hop.
        var status = new Progress<string>(s => _status.Text = s);
        var progress = new Progress<int>(p => _status.Text = $"Transcribing on device… {p}%");

        _sub?.Dispose();
        _sub = _voice.Transcribe(samples, language, progress, status).Subscribe(
            seg => MainThread.BeginInvokeOnMainThread(() =>
            {
                segments.Add(seg.Text);
                _transcript.Text = string.Join(" ", segments);
            }),
            ex => MainThread.BeginInvokeOnMainThread(() => _status.Text = $"Error: {ex.Message}"),
            () => MainThread.BeginInvokeOnMainThread(() => OnTranscribed(string.Join(" ", segments).Trim())));
    }

    private void OnTranscribed(string text)
    {
        _engine.Text = $"engine: {_voice.Engine}";   // CoreML on the Mac GPU, Vulkan on Win/Android, else Cpu
        _engine.IsVisible = true;
        _status.Text = "Transcribed.";
        if (text.Length == 0)
        {
            _status.Text = "Nothing recognised.";
            return;
        }

        // Prefer on-device AI (Apple Intelligence) — fully offline. Otherwise leave the transcript only.
        if (_ai.Availability == OnDeviceChatAvailability.Available)
        {
            _status.Text = "Thinking on-device…";
            _ai.Respond(text).Subscribe(reply => MainThread.BeginInvokeOnMainThread(async () =>
            {
                _reply.Text = reply;
                _reply.IsVisible = true;
                _status.Text = "Reply (on-device).";
                await SpeakInDetectedLanguageAsync(reply, text);
            }));
        }
    }

    /// <summary>
    /// Speaks <paramref name="reply"/> in a voice whose language matches the reply (Swiss-German replies
    /// read as Standard German → a German voice; English → an English voice). Falls back to the input's
    /// language, then to the system default voice. Without this, TTS always used the device's default
    /// locale, so a German answer was spoken by an English voice.
    /// </summary>
    private static async Task SpeakInDetectedLanguageAsync(string reply, string inputFallback)
    {
        var lang = DetectLanguage(reply) ?? DetectLanguage(inputFallback);
        Locale? locale = null;
        if (!string.IsNullOrEmpty(lang))
        {
            var locales = await TextToSpeech.Default.GetLocalesAsync();
            // Match "de" against Locale.Language "de" or "de-DE"; prefer an exact language match.
            locale = locales.FirstOrDefault(l => string.Equals(l.Language, lang, StringComparison.OrdinalIgnoreCase))
                  ?? locales.FirstOrDefault(l => l.Language?.StartsWith(lang + "-", StringComparison.OrdinalIgnoreCase) == true);
        }
        await TextToSpeech.Default.SpeakAsync(reply, locale is null ? null : new SpeechOptions { Locale = locale });
    }

    /// <summary>Detects the dominant language of <paramref name="text"/> as a BCP-47 code ("de", "en", …)
    /// via Apple's NaturalLanguage framework; null when undetermined or off Apple platforms.</summary>
    private static string? DetectLanguage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
#if IOS || MACCATALYST
        var recognizer = new NaturalLanguage.NLLanguageRecognizer();
        recognizer.Process(text);
        // Map the detected NLLanguage to a BCP-47 code for TTS locale matching (Swiss German → "de").
        return recognizer.DominantLanguage switch
        {
            NaturalLanguage.NLLanguage.German => "de",
            NaturalLanguage.NLLanguage.English => "en",
            NaturalLanguage.NLLanguage.French => "fr",
            NaturalLanguage.NLLanguage.Italian => "it",
            NaturalLanguage.NLLanguage.Spanish => "es",
            _ => null,
        };
#else
        return null;
#endif
    }
}
