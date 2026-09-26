using System;
using System.Collections.Immutable;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using MeshWeaver.Fixture;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 MeshWeaver#3876 (reopened 2026-09-26) — a bundle whose resolved bytes are removed before the
/// archive opens them answers <b>503 + <c>Retry-After</c></b>, never an unhandled 500.
///
/// <para>The production shape, measured on memex-cloud (pod
/// <c>memex-portal-deployment-6c7669df84-9b4rz</c>, six occurrences): the bundle route resolved
/// <c>Collaboration_Review</c>'s assembly to <c>/data/assembly-cache/…/v1172-….dll</c>, the assembly
/// store's eviction (newest three versions kept per type) removed it, and
/// <c>NuGetPackageWriter.Write</c> → <c>File.OpenRead</c> threw <c>FileNotFoundException</c> past
/// every handler. Reproduced here byte for byte: a real archive write whose entry opens a real file
/// that was deleted after it was resolved.</para>
/// </summary>
public class BundleServeRaceIsTransientTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-bundle-serve-race-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the temp root.</summary>
    public BundleServeRaceIsTransientTest() => Directory.CreateDirectory(root);

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    /// <summary>The removed file is a 503 with <c>Retry-After</c>.</summary>
    [Theory]
    [InlineData(false)] // the file is gone
    [InlineData(true)]  // its whole directory is gone
    public async Task BytesRemovedBetweenResolveAndOpen_AreTransient(bool directoryGone)
    {
        var ct = TestContext.Current.CancellationToken;
        var http = new DefaultHttpContext();

        var result = await ArchiveOfAResolvedPath(directoryGone)
            .TransientWhenServedBytesMoved(http, "Collaboration", "1.2.3", logger: null)
            .Should().Within(TestTimeouts.Quick)
            .Emit("the removal must become an answer, not an escaping fault", cancellationToken: ct);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
        Assert.Equal("30", http.Response.Headers.RetryAfter.ToString());
    }

    /// <summary>
    /// The unmapped shape — what production ran: the removal escapes the route as the raw
    /// <see cref="FileNotFoundException"/> the exception middleware turned into a 500. Pins that
    /// the archive write really does fault on this input, so the mapping above is not vacuous.
    /// </summary>
    [Fact]
    public async Task WithoutTheMapping_TheRemovalEscapesAsAFault()
    {
        var ct = TestContext.Current.CancellationToken;

        var notification = await ArchiveOfAResolvedPath(directoryGone: false)
            .Materialize()
            .Should().Within(TestTimeouts.Quick)
            .Emit("the archive write must terminate", cancellationToken: ct);

        Assert.Equal(NotificationKind.OnError, notification.Kind);
        Assert.IsType<FileNotFoundException>(notification.Exception);
    }

    /// <summary>Any OTHER I/O fault is a real defect and passes through unchanged.</summary>
    [Fact]
    public async Task AnyOtherIoFault_PassesThrough()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = new DefaultHttpContext();

        var notification = await Observable.Throw<IResult>(new IOException("sharing violation"))
            .TransientWhenServedBytesMoved(http, "Collaboration", "1.2.3", logger: null)
            .Materialize()
            .Should().Within(TestTimeouts.Quick)
            .Emit("the fault must reach the caller", cancellationToken: ct);

        Assert.Equal(NotificationKind.OnError, notification.Kind);
        Assert.Equal("sharing violation", notification.Exception!.Message);
        Assert.Equal(0, http.Response.Headers.RetryAfter.Count);
    }

    /// <summary>
    /// The route's own write, in miniature: resolve a path that exists, then lose it, then write
    /// the archive whose entry opens it — exactly the order the bundle route runs in.
    /// </summary>
    private IObservable<IResult> ArchiveOfAResolvedPath(bool directoryGone)
    {
        var typeDirectory = Path.Combine(root, "Collaboration_Review");
        Directory.CreateDirectory(typeDirectory);
        var resolved = Path.Combine(typeDirectory, "v1172-c003e001-bea6e0b4ca5e.dll");
        File.WriteAllBytes(resolved, [1, 2, 3]);

        // The store's eviction, between the resolve and the open.
        if (directoryGone)
            Directory.Delete(typeDirectory, recursive: true);
        else
            File.Delete(resolved);

        return Observable.Defer(() =>
        {
            var buffer = new MemoryStream();
            NuGetPackageWriter.Write(
                buffer,
                new PluginManifest("Collaboration", "Collaboration", "1.2.3", "Collaboration", null,
                    ImmutableArray<string>.Empty),
                "3.0.0",
                [new NuGetPackageWriter.Entry("lib/Collaboration_Review.dll", () => File.OpenRead(resolved))],
                "{}");
            return Observable.Return(Results.File(buffer, "application/octet-stream"));
        });
    }
}
