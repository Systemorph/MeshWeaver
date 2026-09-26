using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// End-to-end routing of <see cref="NotificationService.Raise"/> on a real mesh: the recipient's
/// per-feature preference decides the channels, a channel the platform cannot deliver itself is
/// handed to the registered <see cref="INotificationChannelDeliverer"/>, and a recipient that
/// channel cannot reach is a SKIP — reported, never an error to the raiser.
///
/// <para>The Teams channel here is a recording deliverer standing in for the Teams module (which
/// lives in MeshWeaver.Plugins): it "reaches" only the people in its connected set, exactly as the
/// real one reaches only people who have messaged the bot.</para>
/// </summary>
public class NotificationFeatureDeliveryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A Teams channel that records what it was handed and reaches only its connected people.</summary>
    private sealed class RecordingTeams(ImmutableHashSet<string> connected) : INotificationChannelDeliverer
    {
        private ImmutableList<NotificationChannelMessage> handed = ImmutableList<NotificationChannelMessage>.Empty;

        public ImmutableList<NotificationChannelMessage> Handed => handed;

        public string Channel => NotificationChannelKind.Teams;

        public IObservable<NotificationChannelResult> Deliver(IMessageHub hub, NotificationChannelMessage message)
            => Observable.Defer(() =>
            {
                ImmutableInterlocked.Update(ref handed, l => l.Add(message));
                return Observable.Return(connected.Contains(message.Recipient)
                    ? NotificationChannelResult.Sent(Channel)
                    : NotificationChannelResult.Skipped(Channel, "not connected to Teams"));
            });
    }

    // Field initializers run before the base constructor calls ConfigureMesh. Instance, never static.
    private readonly RecordingTeams teams = new(["teams_connected", "teams_override"]);

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<INotificationChannelDeliverer>(teams));

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private System.Text.Json.JsonSerializerOptions Json => Mesh.JsonSerializerOptions;

    private async Task CreateUser(string id)
    {
        using (Access.ImpersonateAsSystem())
            await MeshService.CreateNode(new MeshNode(id)
            {
                NodeType = "User",
                Name = id,
                Content = new User { Email = $"{id}@acme.com", FullName = id },
            }).Should().Emit(cancellationToken: TestContext.Current.CancellationToken);
    }

    private static NotificationRequest Approval(string recipient) => new()
    {
        Recipient = recipient,
        MainNodePath = "Ops/Actions/roll-memex",
        TargetNodePath = "Ops/Actions/roll-memex",
        Title = LocalizableText.Verbatim("Approval requested: Roll memex"),
        Message = LocalizableText.Verbatim("A Roll of memex is waiting for a second administrator."),
        Type = NotificationType.ApprovalRequired,
        CreatedBy = "requester",
    };

    private Task<ImmutableList<NotificationChannelResult>> Raise(NotificationRequest request, CancellationToken ct)
        => NotificationService.Raise(Mesh, request).Timeout(TestTimeouts.WriteConvergence).Await(ct);

    [Fact(Timeout = 60000)]
    public async Task ADefaultRecipient_GetsTheBellAndTeams_AndTheBellRowCarriesTheFeature()
    {
        var ct = TestContext.Current.CancellationToken;
        const string recipient = "teams_connected";
        await CreateUser(recipient);

        var report = await Raise(Approval(recipient), ct);

        Assert.Contains(report, r => r.Channel == NotificationChannelKind.InApp && r.Delivered);
        Assert.Contains(report, r => r.Channel == NotificationChannelKind.Teams && r.Delivered);
        var handed = Assert.Single(teams.Handed, m => m.Recipient == recipient);
        Assert.Equal(NotificationFeatures.Approvals, handed.Feature);
        Assert.Equal("Approval requested: Roll memex", handed.Title);
        Assert.Equal("Ops/Actions/roll-memex", handed.TargetNodePath);

        await Mesh.GetWorkspace()
            .GetQuery($"notif|{recipient}", $"path:{recipient}/_Notification scope:children nodeType:Notification")
            .Where(nodes => (nodes ?? []).Any(n =>
                n.ContentAs<Notification>(Json) is { NotificationType: NotificationType.ApprovalRequired } bell
                && bell.FeatureOf() == NotificationFeatures.Approvals
                && bell.Feature == NotificationFeatures.Approvals))
            .FirstAsync().Timeout(TestTimeouts.WriteConvergence).Await();
    }

    [Fact(Timeout = 60000)]
    public async Task ARecipientNotConnectedToTeams_IsSkippedThere_AndStillGetsTheBell_WithoutAnError()
    {
        var ct = TestContext.Current.CancellationToken;
        const string recipient = "teams_absent";
        await CreateUser(recipient);

        // The raise COMPLETES — a channel the recipient cannot be reached on is never the raiser's error.
        var report = await Raise(Approval(recipient), ct);

        var teamsLeg = Assert.Single(report, r => r.Channel == NotificationChannelKind.Teams);
        Assert.False(teamsLeg.Delivered);
        Assert.Equal("not connected to Teams", teamsLeg.Detail);
        Assert.Contains(report, r => r.Channel == NotificationChannelKind.InApp && r.Delivered);
    }

    [Fact(Timeout = 90000)]
    public async Task APerFeatureOverride_TurnsTeamsOffForThatFeatureOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        const string recipient = "teams_override";
        await CreateUser(recipient);

        // The settings tab's path: create-on-absent (seeded with the effective default), then one
        // field flipped through the node stream.
        var path = await NotificationFeaturePreferenceNodeType
            .EnsureExists(Mesh, recipient, NotificationFeatures.Approvals)
            .FirstAsync().Timeout(TestTimeouts.WriteConvergence).Await();
        Assert.Equal(NotificationFeaturePreferencePaths.PathFor(recipient, NotificationFeatures.Approvals), path);
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Update(n => n with
            {
                Content = (n.ContentAs<NotificationFeaturePreference>(Json) ?? new NotificationFeaturePreference())
                    with { Teams = false }
            })
            .FirstAsync().Timeout(TestTimeouts.WriteConvergence).Await();
        // The "off" must survive the merge patch (JsonIgnore(Never)) — else this never arrives.
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n.ContentAs<NotificationFeaturePreference>(Json) is { Teams: false, Bell: true })
            .FirstAsync().Timeout(TestTimeouts.WriteConvergence).Await();

        // The dispatcher takes EXISTENCE from the index (a node that may not exist is never
        // point-read) and the CONTENT from the authoritative stream. So wait only for the index to
        // list the node — a read, not a retried side effect — then raise ONCE: the flipped value
        // must be what it reads, however stale the index's own copy of the content is.
        await WaitIndexed(path);
        var handedBefore = teams.Handed.Count(m => m.Recipient == recipient && m.Feature == NotificationFeatures.Approvals);
        var approvals = await Raise(Approval(recipient), ct);
        Assert.DoesNotContain(approvals, r => r.Channel == NotificationChannelKind.Teams);
        Assert.Contains(approvals, r => r.Channel == NotificationChannelKind.InApp && r.Delivered);
        Assert.Equal(handedBefore,
            teams.Handed.Count(m => m.Recipient == recipient && m.Feature == NotificationFeatures.Approvals));

        // Another feature, with no node of its own, still reaches Teams.
        var chat = await Raise(Approval(recipient) with
        {
            Type = NotificationType.ChatReady,
            Feature = null,
            Title = LocalizableText.Verbatim("Your answer is ready"),
        }, ct);
        Assert.Contains(chat, r => r.Channel == NotificationChannelKind.Teams && r.Delivered);
        Assert.Contains(teams.Handed, m => m.Recipient == recipient && m.Feature == NotificationFeatures.ChatReady);
    }

    /// <summary>Waits until the index lists <paramref name="path"/> — existence, not content.</summary>
    private Task WaitIndexed(string path)
        => Mesh.GetWorkspace()
            .GetQuery($"test-indexed|{path}", $"path:{path} select:path,id,nodeType")
            .Where(nodes => (nodes ?? []).Any(n => n.Path == path))
            .FirstAsync().Timeout(TestTimeouts.WriteConvergence).Await();

    [Fact(Timeout = 90000)]
    public async Task AnUnreadablePreference_FailsClosed_BellOnly_NeverTeams()
    {
        var ct = TestContext.Current.CancellationToken;
        const string recipient = "teams_connected_unreadable";
        await CreateUser(recipient);

        // A preference node that EXISTS but whose content does not read as a preference — the
        // person may have chosen something we cannot see, so Teams must not open on the default.
        var path = NotificationFeaturePreferencePaths.PathFor(recipient, NotificationFeatures.Approvals);
        using (Access.ImpersonateAsSystem())
            await MeshService.CreateNode(new MeshNode(NotificationFeatures.Approvals,
                    NotificationFeaturePreferencePaths.NamespaceFor(recipient))
                {
                    NodeType = NotificationFeaturePreferenceNodeType.NodeType,
                    Name = "unreadable",
                    Content = "not a preference",
                }).Should().Emit(cancellationToken: ct);
        await WaitIndexed(path);

        var report = await Raise(Approval(recipient), ct);

        var only = Assert.Single(report);
        Assert.Equal(NotificationChannelKind.InApp, only.Channel);
        Assert.True(only.Delivered);
        Assert.DoesNotContain(teams.Handed, m => m.Recipient == recipient);
    }

    [Fact(Timeout = 60000)]
    public async Task AFeatureKeyOutsideTheAlphabet_IsRejected_AndNothingIsDelivered()
    {
        var ct = TestContext.Current.CancellationToken;
        const string recipient = "teams_connected_badkey";
        await CreateUser(recipient);

        var raise = () => NotificationService.Raise(Mesh, Approval(recipient) with { Feature = "ops/a" })
            .Timeout(TestTimeouts.WriteConvergence).Await(ct);

        await Assert.ThrowsAsync<ArgumentException>(raise);
        Assert.DoesNotContain(teams.Handed, m => m.Recipient == recipient);
    }

    [Fact(Timeout = 60000)]
    public async Task AnExplicitModuleFeature_IsRoutedByItsOwnKey_WithTheDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        const string recipient = "teams_connected";
        await CreateUser(recipient + "_feature");

        var report = await Raise(Approval(recipient + "_feature") with
        {
            Type = NotificationType.System,
            Feature = NotificationFeatures.Triage,
        }, ct);

        Assert.Contains(report, r => r.Channel == NotificationChannelKind.InApp && r.Delivered);
        Assert.Contains(report, r => r.Channel == NotificationChannelKind.Teams);
        Assert.Contains(teams.Handed, m => m.Recipient == recipient + "_feature" && m.Feature == NotificationFeatures.Triage);
    }

    [Fact(Timeout = 60000)]
    public async Task APlatformNotification_ReachesOnlyTheOperatorsBell()
    {
        var ct = TestContext.Current.CancellationToken;
        var report = await Raise(Approval("anyone") with { Recipient = null }, ct);

        var only = Assert.Single(report);
        Assert.Equal(NotificationChannelKind.InApp, only.Channel);
        Assert.DoesNotContain(teams.Handed, m => m.Recipient == NotificationService.PlatformAddressee);
    }
}

/// <summary>
/// Two deliverers for ONE channel, the first of which throws — synchronously in <c>Deliver</c>.
/// Each deliverer is isolated before the merge, so the other's success still delivers the channel.
/// </summary>
public class NotificationDelivererIsolationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private sealed class Throwing : INotificationChannelDeliverer
    {
        public string Channel => NotificationChannelKind.Teams;
        public IObservable<NotificationChannelResult> Deliver(IMessageHub hub, NotificationChannelMessage message)
            => throw new InvalidOperationException("this deliverer is broken");
    }

    private sealed class Faulting : INotificationChannelDeliverer
    {
        public string Channel => NotificationChannelKind.Teams;
        public IObservable<NotificationChannelResult> Deliver(IMessageHub hub, NotificationChannelMessage message)
            => Observable.Throw<NotificationChannelResult>(new InvalidOperationException("this one faults later"));
    }

    private sealed class Working : INotificationChannelDeliverer
    {
        public int Calls;
        public string Channel => NotificationChannelKind.Teams;
        public IObservable<NotificationChannelResult> Deliver(IMessageHub hub, NotificationChannelMessage message)
            => Observable.Defer(() =>
            {
                Interlocked.Increment(ref Calls);
                return Observable.Return(NotificationChannelResult.Sent(Channel));
            });
    }

    // Field initializers run before the base constructor calls ConfigureMesh. Instance, never static.
    private readonly Working working = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                .AddSingleton<INotificationChannelDeliverer>(new Throwing())
                .AddSingleton<INotificationChannelDeliverer>(new Faulting())
                .AddSingleton<INotificationChannelDeliverer>(working));

    [Fact(Timeout = 60000)]
    public async Task OneThrowingDeliverer_CannotMaskAnothersSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        const string recipient = "isolation_user";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (access.ImpersonateAsSystem())
            await mesh.CreateNode(new MeshNode(recipient)
            {
                NodeType = "User",
                Name = recipient,
                Content = new User { Email = $"{recipient}@acme.com", FullName = recipient },
            }).Should().Emit(cancellationToken: ct);

        var report = await NotificationService.Raise(Mesh, new NotificationRequest
            {
                Recipient = recipient,
                MainNodePath = recipient,
                Title = LocalizableText.Verbatim("Approval requested"),
                Message = LocalizableText.Verbatim("please approve"),
                Type = NotificationType.ApprovalRequired,
            })
            .Timeout(TestTimeouts.WriteConvergence).Await(ct);

        var teamsLeg = Assert.Single(report, r => r.Channel == NotificationChannelKind.Teams);
        Assert.True(teamsLeg.Delivered, $"the working deliverer delivered; got: {teamsLeg.Detail}");
        Assert.Equal(1, working.Calls);
    }
}
