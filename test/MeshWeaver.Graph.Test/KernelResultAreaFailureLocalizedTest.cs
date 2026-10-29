using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#7363: when a cell fails, the kernel writes the failure into the cell's RESULT AREA —
/// the surface the embedded cell shows under its code. Its heading is platform text and the
/// saturation refusal is a platform sentence, so both render in the language of the viewer who ran
/// the cell. They used to be the English literal <c>**Execution failed**</c> and the exception's
/// English text, whatever the viewer's language.
///
/// <para>The rendered area is read off the real Roslyn kernel's area stream — not the transcript,
/// which was already keyed and is a separate surface.</para>
/// </summary>
public class KernelResultAreaFailureLocalizedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static TimeSpan Bound => TestTimeouts.Convergence;

    /// <summary>
    /// A German viewer runs a script that throws: the result area's heading is German, and the
    /// script's own message — script output — is kept as written.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AFailedCell_ShowsItsResultAreaHeadingInTheViewersLanguage()
    {
        var ct = TestContext.Current.CancellationToken;
        var activity = await CreateActivity(ct);
        var viewId = Guid.NewGuid().ToString("N");

        RequestHub.Post(
            new SubmitCodeRequest("throw new System.InvalidOperationException(\"boom from the cell\");")
            {
                Id = viewId,
                ActivityLogPath = activity,
            },
            o => o.WithTarget(new Address(activity)).WithAccessContext(TestUsers.Admin with { Locale = "de" }));

        var markdown = await ResultArea(activity, viewId, ct);

        markdown.Should().StartWith("**Ausführung fehlgeschlagen**",
            "the heading is platform text and the viewer who ran the cell reads German");
        markdown.Should().Contain("boom from the cell", "the script's own message renders as written");
        markdown.Should().NotContain("Execution failed");
    }

    /// <summary>
    /// The refusal of a full kernel script lane cannot be provoked by a script without parking every
    /// slot of the process, so its rendering is asserted on the view the kernel writes for it.
    /// </summary>
    [Fact]
    public void ASaturationRefusal_RendersThePlatformSentenceInTheViewersLanguage()
    {
        var de = Markdown(KernelExecutor.FailureView(new IoPoolSaturatedException(4), "de"));
        de.Should().StartWith("**Ausführung fehlgeschlagen**");
        de.Should().Contain("alle 4 Kernel-Plätze", "the refusal renders through activity.codeRun.kernelSaturated");
        de.Should().NotContain("Refused:", "the exception's English text is for the host's diagnostics only");

        var en = Markdown(KernelExecutor.FailureView(new IoPoolSaturatedException(4), "en"));
        en.Should().StartWith("**Execution failed**");
        en.Should().Contain("all 4 kernel slot(s)");
    }

    private static string Markdown(object? control)
        => control.Should().BeOfType<MarkdownControl>().Subject.Markdown!.ToString()!;

    private async Task<string> ResultArea(string activityPath, string viewId, CancellationToken ct)
    {
        // The run settles its Activity after it has written the result area, so a terminal Activity
        // means the area holds the failure view.
        await Mesh.GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(node => node?.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions))
            .Where(log => log is not null && log.Status.IsTerminal())
            .FirstAsync()
            .Timeout(Bound)
            .Await(ct);

        var host = Mesh.GetHostedHub(new Address(activityPath), HostedHubCreation.Never);
        host.Should().NotBeNull("the Activity's kernel host ran the cell");
        var view = await host!.ServiceProvider
            .GetRequiredService<ISynchronizationStream<ImmutableDictionary<string, object>>>()
            .Select(change => change.Value?.GetValueOrDefault(viewId))
            .Where(v => v is not null)
            .FirstAsync()
            .Timeout(Bound)
            .Await(ct);
        return Markdown(view);
    }

    private async Task<string> CreateActivity(CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var ns = $"{TestPartition}/_Activity";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access
            .RunAsSystem(() => mesh.CreateNode(new MeshNode(id, ns)
            {
                Name = "localized failure activity",
                NodeType = "Activity",
                MainNode = TestPartition,
                State = MeshNodeState.Active,
                Content = new ActivityLog("ScriptExecution") { Id = id, Status = ActivityStatus.Running },
            }))
            .FirstAsync()
            .Timeout(Bound)
            .Await(ct);
        return $"{ns}/{id}";
    }
}
