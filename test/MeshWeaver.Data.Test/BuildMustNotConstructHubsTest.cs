using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// 🚨 <b>#1868 — <c>Build</c> must not construct another hub.</b>
///
/// <para><b>The fact.</b> <c>MessageHubConfiguration.Build</c> runs <c>SyncBuildupActions</c>
/// inline, before <c>StartMessageProcessing()</c>. Two of those actions reached code that creates
/// hubs: <c>DataExtensions.GetDefaultConfiguration</c> registered
/// <c>WithInitialization(h =&gt; h.GetWorkspace())</c> on the SYNCHRONOUS overload
/// (→ <c>Workspace..ctor</c> → <c>DataContext.Initialize</c> → <c>DataSource.GetStream</c> →
/// <c>SynchronizationStream..ctor</c>), and <c>KernelContainer</c> registered
/// <c>StartActivityControlPlane</c> the same way (→ <c>WatchControlPlane</c> →
/// <c>AcquireStream</c> → <c>Workspace.GetStream</c>). <c>SynchronizationStream</c>'s constructor
/// ALWAYS calls <c>Host.GetHostedHub(sync/…, HostedHubCreation.Always)</c>, so every data-enabled
/// hub built at least one sub-hub — and a second Autofac container — inside its own <c>Build</c>.
/// Measured with a depth counter on a GREEN <c>MeshWeaver.FutuRe.Test</c> run: <b>1,350 nested
/// Builds</b>, all depth=2, top outers <c>FutuRe/Analysis</c> ×156, <c>FutuRe/EuropeRe/Analysis</c>
/// ×150, <c>FutuRe</c> ×132, <c>mesh</c> ×92.</para>
///
/// <para><b>What is and is not claimed.</b> NOT that this causes a SIGSEGV — that hypothesis was
/// investigated under #613 and falsified (the faulting thread is the background GC thread with zero
/// managed frames), and withdrawn in #1867; 1,350 nestings per green run refute it. What survives is
/// a design defect worth judging on its own: <i>a disposal that races a construction races a TREE of
/// constructions, not one frame.</i> That is the shape behind the whole shutdown-race family — #645
/// (cascade the creation freeze through the subtree), #715 (finish in-flight constructions at
/// disposal instead of racing them), #967 (unload node ALCs at the end of teardown), #1573 (the
/// 8-issue family) — each of which had to widen its guard to cover work started by a construction
/// that had itself been started by a construction. <c>HostedHubsCollection</c>'s in-flight counter
/// tracks the OUTER creation; the inner one it spawns is a second entry, on the same thread, whose
/// refusal/finish semantics are only correct because the guards were extended by hand, one incident
/// at a time.</para>
///
/// <para><b>Scope.</b> This pins step 1 of the adoption: no hub is constructed from inside
/// <c>Build</c>. Step 2 — stopping <c>SynchronizationStream</c>'s constructor from creating its
/// sub-hub eagerly — is deliberately not attempted (~96 sites dereference
/// <c>ISynchronizationStream.Hub</c> as non-null, and the constructor's "a stream that cannot own
/// its sub-hub is not a stream — refuse, never fabricate" contract is worth preserving). Step 1
/// alone does not remove the sub-hub; it moves its creation OUT of <c>Build</c>, which is the part
/// the disposal guards care about.</para>
/// </summary>
public class BuildMustNotConstructHubsTest : HubTestBase
{
    /// <summary>Test record for the data source below — a source is required, since a DataContext
    /// with no sources creates no stream and therefore no sub-hub either way.</summary>
    private record Widget(string Id, string Text);

    public BuildMustNotConstructHubsTest(ITestOutputHelper output) : base(output)
    {
        // Nothing to wire: the violations are recorded on the hub instance
        // (MessageHub.HubsConstructedDuringBuild), not fished out of the log.
    }

    // 🚨 A HUB SOURCE, deliberately. HubDataSource.Initialize EAGERLY opens its remote stream
    // (GetRemoteStreamAsHub → CreateExternalClient → new SynchronizationStream → GetHostedHub(sync/…,
    // Always)), so this is the shape that actually reaches hub construction from a buildup action.
    // A plain in-memory source's Initialize is a no-op and would make the test vacuous.
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddData(data => data.AddHubSource(CreateHostAddress(),
                source => source.WithType<Widget>(type => type.WithKey(w => w.Id))));

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(source =>
                source.WithType<Widget>(type => type
                    .WithKey(w => w.Id)
                    .WithInitialData(() => Observable.Return<IEnumerable<Widget>>([new Widget("1", "A")])))));

    /// <summary>
    /// Building a data-enabled hub must construct no hub of its own.
    ///
    /// <para><b>Non-vacuity.</b> On <c>origin/main</c> the workspace is resolved from a SYNCHRONOUS
    /// buildup action, so the very first data-enabled hub logs the violation naming its
    /// <c>sync/…</c> child, and this assertion fails with those addresses in the message.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task DataEnabledHub_BuildsNoHubOfItsOwn()
    {
        // Force the hubs into existence and drive them far enough that their workspaces really are
        // built — otherwise "no nesting" could just mean "nothing was initialized".
        var client = (MessageHub)GetClient();
        var host = (MessageHub)GetHost();
        client.ServiceProvider.GetRequiredService<IWorkspace>().Should().NotBeNull(
            "the workspace must actually be constructed, or this proves nothing");
        await client.Started.WaitAsync(30.Seconds());
        await host.Started.WaitAsync(30.Seconds());

        // The premise, asserted: the hub-backed data source really did open its remote stream, so a
        // sync/ sub-hub really was created. Without this, "constructed nothing during Build" would
        // also be satisfied by a hub that constructed nothing at all.
        var hostedTree = client.GetDisposalDiagnostics() + host.GetDisposalDiagnostics();
        hostedTree.Should().Contain("sync/",
            "the hub-backed data source must have opened its remote stream (HubDataSource.Initialize "
            + "→ GetRemoteStreamAsHub → new SynchronizationStream → GetHostedHub(sync/…)), or there "
            + "is nothing that COULD have nested");

        var violations = client.HubsConstructedDuringBuild.Concat(host.HubsConstructedDuringBuild)
            .Select(a => a.ToString()).ToArray();
        foreach (var v in violations)
            Output.WriteLine(v);

        violations.Should().BeEmpty(
            "a SyncBuildupAction reached hub construction: Build runs those inline, before "
            + "StartMessageProcessing, so a disposal racing this hub's creation races a TREE of "
            + "constructions rather than one frame (#1868). The initialization belongs on the "
            + "OBSERVABLE WithInitialization overload, which runs on InitializeHubRequest after "
            + "Build has returned. Constructed during Build: " + string.Join(", ", violations));
    }

}
