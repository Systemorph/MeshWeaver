using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Speech;

/// <summary>Registers the centralized <see cref="ISpeechTranscriber"/> (the Whisper container client).</summary>
public static class SpeechServiceExtensions
{
    /// <summary>
    /// Binds <see cref="SpeechConfiguration"/> from the <c>Speech</c> section and registers a singleton
    /// <see cref="ISpeechTranscriber"/> backed by the Whisper container. Config is read live via
    /// <see cref="IOptionsMonitor{T}"/>, so a portal edit (reloaded config) takes effect without a restart.
    /// </summary>
    public static IServiceCollection AddSpeechTranscription(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SpeechConfiguration.SectionName);
        services.Configure<SpeechConfiguration>(section.Bind);
        return services.AddSpeechTranscription();
    }

    /// <summary>Registers the transcriber without binding config (the caller has already <c>Configure</c>d it).</summary>
    public static IServiceCollection AddSpeechTranscription(this IServiceCollection services)
    {
        services.AddSingleton<ISpeechTranscriber>(sp => new WhisperContainerTranscriber(
            // One long-lived HttpClient for the singleton (correct pattern for a service lifetime); the
            // per-call timeout is applied via a linked token, not by mutating this client.
            new HttpClient(),
            sp.GetService<IoPoolRegistry>(),
            () => sp.GetRequiredService<IOptionsMonitor<SpeechConfiguration>>().CurrentValue,
            sp.GetRequiredService<ILogger<WhisperContainerTranscriber>>()));
        return services;
    }
}
