using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;

namespace Memex.Client.Services;

/// <summary>Whether on-device text generation is usable right now (hardware + OS + model state).</summary>
public enum OnDeviceChatAvailability { Available, Unavailable }

/// <summary>
/// On-device text generation. On iPhone + Apple-silicon Macs this is <b>Apple Intelligence</b> (the OS
/// FoundationModels) — the app ships <b>no</b> LLM (only the Swiss-German Whisper model). When the platform
/// or hardware can't provide it, <see cref="Availability"/> is <c>Unavailable</c> and <see cref="Respond"/>
/// yields an empty stream so callers fall back to the connected mesh. Reactive + run off the UI thread.
/// </summary>
public interface IOnDeviceChat
{
    OnDeviceChatAvailability Availability { get; }

    /// <summary>The model's reply to a single prompt (whole reply; empty when unavailable). Runs on the IoPool.</summary>
    IObservable<string> Respond(string prompt);
}

/// <summary>Picks the platform implementation: Apple Intelligence on iOS/MacCatalyst, else unavailable.</summary>
public static class OnDeviceChat
{
    public static IOnDeviceChat Create(IIoPool pool) =>
#if IOS || MACCATALYST
        new AppleIntelligenceChat(pool);
#else
        new UnavailableOnDeviceChat();
#endif
}

/// <summary>No on-device model on this platform — the app uses the connected mesh for AI.</summary>
internal sealed class UnavailableOnDeviceChat : IOnDeviceChat
{
    public OnDeviceChatAvailability Availability => OnDeviceChatAvailability.Unavailable;
    public IObservable<string> Respond(string prompt) => Observable.Empty<string>();
}
