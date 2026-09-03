using System.Reactive.Linq;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 Pins the ONE line that tells "notified nobody" from "nobody needed notifying" (#2235,
/// Memex#140). A framework-release broadcast with zero subscribers has two causes with identical
/// outcomes — <c>0 dispatched, 0 failed</c>: on a mesh that does not receive platform-build
/// deliveries it is the correct, permanent state; on the CONTROL instance it means the release wave
/// silently never runs and every satellite falls back to its schedule poll. memex-cloud sat in the
/// second state for four days with the slots rendered and blank, and nothing was red.
///
/// <para>The control instance is identified by configuration alone — its
/// <c>WebhookInbox:Targets</c> allowlist carries <see cref="FrameworkBroadcastOptions.PlatformBuildsTarget"/>,
/// which is what makes it the mesh that receives release events. The broadcaster reads that
/// section itself (<c>IConfiguration</c> is injected for exactly this), so the level of the line
/// depends on nothing but the deployment's own config: WARNING naming where a subscriber is declared
/// on the control instance, INFORMATION everywhere else. These tests fail if either side regresses —
/// a warning that fires on every non-control mesh is noise nobody reads, and an Information line on
/// the control instance is the four silent days again.</para>
///
/// <para>Since 2026-09-03 the subscriber set is DATA IN THE MESH — the repositories the
/// <c>Hosting/Deployment</c> records serve as registry sources — derived by the Hosting module's
/// <c>PlatformBuildInboxWatcher</c> and passed to <c>Broadcast</c>. The warning therefore sends the
/// reader to the records, never to the retired <c>FrameworkBroadcast__Subscribers__N</c> slots: a
/// key nothing reads, named in a warning, is the next silent misconfiguration.</para>
/// </summary>
public class FrameworkBroadcastEmptySubscribersGuard
{
    [Fact]
    public void OnTheControlInstance_ZeroSubscribers_IsAWarningNamingWhereASubscriberIsDeclared()
    {
        var logger = new CapturingLogger();
        using var pools = new IoPoolRegistry();
        var broadcaster = Create(pools, logger, controlInstance: true);

        BroadcastOutcome? outcome = null;
        broadcaster.Broadcast(subscribers: null).Subscribe(o => outcome = o);

        Assert.NotNull(outcome);
        Assert.Empty(outcome!.Results);   // nothing is dispatched either way — the point is the LEVEL
        var warning = Assert.Single(logger.Lines, l => l.Level == LogLevel.Warning).Message;
        Assert.Contains(FrameworkBroadcastOptions.PlatformBuildsTarget, warning);
        // The warning must name where a subscriber IS declared — the Deployment record's registry
        // sources — and must NOT name the retired config slots, or the reader is sent to a dead key.
        Assert.Contains("Hosting/Deployment", warning);
        Assert.Contains("isRegistrySource", warning);
        Assert.DoesNotContain("FrameworkBroadcast__Subscribers", warning);
        Assert.DoesNotContain(logger.Lines, l => l.Level == LogLevel.Information);   // never "the normal state" here
    }

    [Fact]
    public void OnANonControlMesh_ZeroSubscribers_IsTheNormalState()
    {
        var logger = new CapturingLogger();
        using var pools = new IoPoolRegistry();
        var broadcaster = Create(pools, logger, controlInstance: false);

        BroadcastOutcome? outcome = null;
        broadcaster.Broadcast(subscribers: null).Subscribe(o => outcome = o);

        Assert.NotNull(outcome);
        Assert.Empty(outcome!.Results);
        // A mesh that does not receive platform-build deliveries is CORRECTLY inert — a warning there is noise.
        Assert.DoesNotContain(logger.Lines, l => l.Level == LogLevel.Warning);
        Assert.Single(logger.Lines, l => l.Level == LogLevel.Information);
    }

    /// <summary>
    /// The predicate is the NORMALIZED target, not a string match: a deployment's allowlist may
    /// carry the path with a leading slash or different casing and is still the control instance.
    /// </summary>
    [Fact]
    public void TheControlInstancePredicate_NormalizesTheTarget()
    {
        var logger = new CapturingLogger();
        using var pools = new IoPoolRegistry();
        var broadcaster = Create(pools, logger, controlInstance: true, target: "/hosting/platformbuilds/");

        broadcaster.Broadcast(subscribers: null).Subscribe(_ => { });

        Assert.Single(logger.Lines, l => l.Level == LogLevel.Warning);
    }

    /// <summary>
    /// Builds the broadcaster as the portal wires it, with the App CONFIGURED (so the zero-subscriber
    /// branch is reached rather than the "App not configured" one) and the empty subscriber list the
    /// default options carry — the exact shape of a deployment whose slots rendered blank.
    /// </summary>
    private static FrameworkReleaseBroadcaster Create(
        IoPoolRegistry pools, CapturingLogger logger, bool controlInstance,
        string target = FrameworkBroadcastOptions.PlatformBuildsTarget)
    {
        var appOptions = Options.Create(new GitHubAppOptions { ClientId = "Iv1.test", PrivateKey = "not-a-real-key" });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(controlInstance
                ? new Dictionary<string, string?>
                {
                    [$"{WebhookInbox.TargetsConfigSection}:0"] = "Store/Payments",
                    [$"{WebhookInbox.TargetsConfigSection}:1"] = target,
                }
                : new Dictionary<string, string?> { [$"{WebhookInbox.TargetsConfigSection}:0"] = "Store/Payments" })
            .Build();
        return new FrameworkReleaseBroadcaster(
            new GitHubAppTokenService(pools, appOptions),
            pools,
            appOptions,
            Options.Create(new FrameworkBroadcastOptions()),
            configuration,
            logger);
    }

    /// <summary>Captures every line with its level so the LEVEL — the whole point — can be asserted.</summary>
    private sealed class CapturingLogger : ILogger<FrameworkReleaseBroadcaster>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }
}
