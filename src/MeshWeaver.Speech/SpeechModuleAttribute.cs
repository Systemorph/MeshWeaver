using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;

[assembly: MeshWeaver.Speech.SpeechModule]

namespace MeshWeaver.Speech;

/// <summary>
/// Module registration for centralized speech-to-text. Listing this DLL under
/// <c>Modules:Assemblies</c> registers the Whisper-container <see cref="ISpeechTranscriber"/>,
/// binding <see cref="SpeechConfiguration"/> from the host's <c>Speech</c> section through the
/// options pipeline. Unconfigured deployments stay inert (<see cref="ISpeechTranscriber.IsConfigured"/>
/// false ⇒ the mic UI hides and <c>POST /api/speech/transcribe</c> answers 503); a deployment
/// WITHOUT the module behaves identically — the endpoint and the chat view both resolve the
/// transcriber optionally.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class SpeechModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<MeshNode> Nodes =>
    [
        new MeshNode("MeshWeaver.Speech")
        {
            Name = "Speech transcription",
            NodeType = "ModuleDefinition",
        }
        .WithGlobalServiceRegistry(services => services.AddSpeechTranscriptionFromConfiguration()),
    ];
}
