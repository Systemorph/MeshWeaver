using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A node picture is announced to the content observers ONCE per new picture
/// (<see cref="IContentUploadObserver.OnFilesWritten"/>), so the content index can describe it when an
/// image describer is configured and record why not when none is — node pictures used to be the one
/// image nobody announced. Every upload is a NEW file (a fresh generated name), so one announcement
/// per upload is one per picture. The refused reader is the control: a picture that was never stored
/// is never announced. Runs on the same gated mesh as <see cref="PictureUploadIsAuthorizedBeforeSaveTest"/>
/// (whose own cases run here too, unchanged).
/// </summary>
public class PictureUploadIsAnnouncedTest(ITestOutputHelper output)
    : PictureUploadIsAuthorizedBeforeSaveTest(output)
{
    private readonly PictureRecorder recorder = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureGate(MeshBuilder builder)
        => base.ConfigureGate(builder)
            .ConfigureServices(s => s.AddSingleton<IContentUploadObserver>(recorder));

    [Fact(Timeout = 120_000)]
    public async Task AnUploadedPicture_IsAnnouncedOnce_AsItsCollectionFile()
    {
        var outcome = await UploadBuiltAsSubscribedAsHub("picturegate-editor")
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind != NotificationKind.OnCompleted,
                "the editor's upload must answer", TestContext.Current.CancellationToken);
        outcome.Kind.Should().Be(NotificationKind.OnNext, $"{outcome.Exception}");

        recorder.Written.Should().ContainSingle("one announcement per new picture").Which.Should().Be(
            $"PictureGateTarget/{ContentCollectionsExtensions.DefaultCollectionName}|{NodeImageUpload.ManagedFilePath(outcome.Value)}",
            "the picture is keyed exactly as an upload of the same file into the node's collection is");
    }

    [Fact(Timeout = 120_000)]
    public async Task ARefusedPicture_IsNeverAnnounced()
    {
        var outcome = await UploadBuiltAsSubscribedAsHub("picturegate-reader")
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind != NotificationKind.OnCompleted,
                "the reader's upload must answer", TestContext.Current.CancellationToken);

        outcome.Kind.Should().Be(NotificationKind.OnError);
        recorder.Written.Should().BeEmpty("a picture that was never stored is never announced");
    }

    private sealed class PictureRecorder : IContentUploadObserver
    {
        private ImmutableList<string> written = ImmutableList<string>.Empty;

        public ImmutableList<string> Written => written;

        public void OnUploaded(string collectionPath, string filePath) { }

        public void OnFilesWritten(string collectionPath, IReadOnlyList<string> filePaths)
            => ImmutableInterlocked.Update(ref written,
                list => list.AddRange(filePaths.Select(p => $"{collectionPath}|{p}")));
    }
}
