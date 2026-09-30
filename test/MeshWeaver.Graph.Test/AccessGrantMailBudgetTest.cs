using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the structural cap on access-grant mail (<see cref="AccessGrantMailBudget"/>): per granter,
/// at most <see cref="AccessGrantMailBudget.MailPerWindow"/> access-granted notifications per window
/// may leave the bell; the rest reach the bell only and the granter is told ONCE. The claim is a
/// create on a deterministic path, so it holds for concurrent claims exactly as it does for
/// sequential ones — which is what a per-process counter could not promise across replicas.
/// </summary>
public class AccessGrantMailBudgetTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A Teams channel that records what it was handed and reaches everyone.</summary>
    private sealed class RecordingTeams : INotificationChannelDeliverer
    {
        private ImmutableList<NotificationChannelMessage> handed = ImmutableList<NotificationChannelMessage>.Empty;

        public ImmutableList<NotificationChannelMessage> Handed => handed;

        public string Channel => NotificationChannelKind.Teams;

        public IObservable<NotificationChannelResult> Deliver(IMessageHub hub, NotificationChannelMessage message)
            => Observable.Defer(() =>
            {
                ImmutableInterlocked.Update(ref handed, l => l.Add(message));
                return Observable.Return(NotificationChannelResult.Sent(Channel));
            });
    }

    // Field initializers run before the base constructor calls ConfigureMesh. Instance, never static.
    private readonly RecordingTeams teams = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<INotificationChannelDeliverer>(teams));

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    // A fixed moment in the middle of a window, so "+1 minute" stays in it.
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 10, 52, 0, TimeSpan.Zero);

    private Task<AccessGrantMailVerdict> Claim(string granter, int grant, DateTimeOffset now, CancellationToken ct)
        => Access.RunAsSystem(() => AccessGrantMailBudget.Claim(
                MeshService, granter, $"Parties/_Access/user{grant}_Access", now))
            .Timeout(TestTimeouts.WriteConvergence)
            .Await(ct);

    /// <summary>One reading of a query, as System (the Admin partition and every bell are the platform's).</summary>
    private IObservable<IReadOnlyList<MeshNode>> QueryOnce(string query)
        => Access.RunAsSystem(() => MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
                .Where(c => c.ChangeType == QueryChangeType.Initial)
                .Select(c => (IReadOnlyList<MeshNode>)c.Items.ToArray())
                .Take(1));

    /// <summary>Re-queries until <paramref name="predicate"/> holds — the index trails the store, never a sleep.</summary>
    private Task<IReadOnlyList<MeshNode>> QueryUntil(string query, Func<IReadOnlyList<MeshNode>, bool> predicate, CancellationToken ct)
        => Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => QueryOnce(query))
            .Where(predicate)
            .FirstAsync()
            .Timeout(TestTimeouts.WriteConvergence)
            .Await(ct);

    private Task CreateAsSystem(MeshNode node, CancellationToken ct)
        => Access.RunAsSystem(() => MeshService.CreateNode(node))
            .FirstAsync().Timeout(TestTimeouts.WriteConvergence).Await(ct);

    private static MeshNode UserNode(string id) => new(id)
    {
        NodeType = "User",
        Name = id,
        Content = new User { Email = $"{id}@acme.com", FullName = id },
    };

    [Fact(Timeout = 90000)]
    public async Task TheFirstThreeGrantsMayMail_TheFourthTellsTheGranter_TheRestAreBellOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var verdicts = new List<AccessGrantMailVerdict>();
        for (var i = 1; i <= 6; i++)
            verdicts.Add(await Claim("alice", i, Now.AddSeconds(i), ct));

        Assert.Equal(
            [
                AccessGrantMailVerdict.Mail, AccessGrantMailVerdict.Mail, AccessGrantMailVerdict.Mail,
                AccessGrantMailVerdict.BellOnlyAndTellGranter,
                AccessGrantMailVerdict.BellOnly, AccessGrantMailVerdict.BellOnly,
            ],
            verdicts);
    }

    [Fact(Timeout = 90000)]
    public async Task ConcurrentGrants_StillSpendExactlyTheBudget_AndTellTheGranterOnce()
    {
        // The Parties shape: many grants by one granter at once. Whatever the interleaving, the
        // deterministic slot paths admit exactly MailPerWindow mails and exactly one notice.
        var ct = TestContext.Current.CancellationToken;
        var verdicts = await Task.WhenAll(Enumerable.Range(1, 12).Select(i => Claim("carol", i, Now, ct)));

        Assert.Equal(AccessGrantMailBudget.MailPerWindow, verdicts.Count(v => v == AccessGrantMailVerdict.Mail));
        Assert.Single(verdicts, v => v == AccessGrantMailVerdict.BellOnlyAndTellGranter);
        Assert.Equal(12 - AccessGrantMailBudget.MailPerWindow - 1, verdicts.Count(v => v == AccessGrantMailVerdict.BellOnly));
    }

    [Fact(Timeout = 90000)]
    public async Task EachGranterHasTheirOwnBudget_AndANewWindowStartsAFreshOne()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 1; i <= 4; i++)
            await Claim("dave", i, Now, ct);

        // Another granter in the same window is untouched by dave's burst.
        Assert.Equal(AccessGrantMailVerdict.Mail, await Claim("erin", 1, Now, ct));
        // dave is over budget now…
        Assert.Equal(AccessGrantMailVerdict.BellOnly, await Claim("dave", 5, Now.AddMinutes(1), ct));
        // …and has a fresh budget in the next window.
        Assert.Equal(AccessGrantMailVerdict.Mail, await Claim("dave", 6, Now + AccessGrantMailBudget.Window, ct));
    }

    [Fact(Timeout = 90000)]
    public async Task TheSweep_KeepsThePreviousWindow_AndDeletesOnlyOlderOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        var w0 = AccessGrantMailBudget.WindowKey(Now);
        var w1 = AccessGrantMailBudget.WindowKey(Now + AccessGrantMailBudget.Window);
        var w2 = AccessGrantMailBudget.WindowKey(Now + 2 * AccessGrantMailBudget.Window);
        var query = $"namespace:{AccessGrantMailBudget.NamespaceFor("frank")} nodeType:{AccessGrantMailBudget.NodeType}";

        for (var i = 1; i <= 4; i++)
            await Claim("frank", i, Now, ct);
        await QueryUntil(query, rows => rows.Count(n => n.Id.StartsWith(w0 + "-", StringComparison.Ordinal)) == 4, ct);

        // The first slot of the NEXT window sweeps — and must leave w0 alone: a claim dated in the
        // previous window can still be in flight, and deleting its slots would let it re-win slot 1.
        Assert.Equal(AccessGrantMailVerdict.Mail, await Claim("frank", 5, Now + AccessGrantMailBudget.Window, ct));
        var afterW1 = await QueryUntil(query, rows => rows.Any(n => n.Id.StartsWith(w1 + "-", StringComparison.Ordinal)), ct);
        Assert.Equal(4, afterW1.Count(n => n.Id.StartsWith(w0 + "-", StringComparison.Ordinal)));
        // …and an in-flight claim of w0 still finds its budget spent.
        Assert.Equal(AccessGrantMailVerdict.BellOnly, await Claim("frank", 6, Now.AddMinutes(1), ct));

        // Two windows on, w0 is older than the previous window and is swept; w1 is kept.
        Assert.Equal(AccessGrantMailVerdict.Mail, await Claim("frank", 7, Now + 2 * AccessGrantMailBudget.Window, ct));
        var afterW2 = await QueryUntil(query,
            rows => rows.All(n => !n.Id.StartsWith(w0 + "-", StringComparison.Ordinal))
                    && rows.Any(n => n.Id.StartsWith(w2 + "-", StringComparison.Ordinal)), ct);
        Assert.Contains(afterW2, n => n.Id == $"{w1}-1");
    }

    [Fact(Timeout = 90000)]
    public async Task TheNotifier_ABulkGrantMailsThreeRecipients_AndTellsTheGranterOnce()
    {
        // The PRODUCTION path end to end: AccessAssignment creations reach the notifier through
        // the change feed, it claims the budget as System and raises through NotificationService.
        var ct = TestContext.Current.CancellationToken;
        const string granter = "grace";
        var recipients = Enumerable.Range(1, 5).Select(i => $"grantee{i}").ToArray();
        await CreateAsSystem(UserNode(granter), ct);
        foreach (var r in recipients)
            await CreateAsSystem(UserNode(r), ct);
        await CreateAsSystem(new MeshNode("Shared") { NodeType = "Markdown", Name = "Shared" }, ct);

        using var notifier = new AccessGrantNotifier(
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>(),
            Access);
        await notifier.StartAsync(ct);

        foreach (var r in recipients)
            await CreateAsSystem(new MeshNode($"{r}_Access", "Shared/_Access")
            {
                NodeType = "AccessAssignment",
                CreatedBy = granter,
                Content = new AccessAssignment
                {
                    AccessObject = r,
                    Roles = [new RoleAssignment { Role = "Viewer" }],
                },
            }, ct);

        // Every recipient is belled, whatever the budget says…
        foreach (var r in recipients)
            await QueryUntil(NotificationService.BellQuery(r), rows => rows.Count > 0, ct);
        // …the granter is told once that the rest went out quietly…
        var told = await QueryUntil(NotificationService.BellQuery(granter), rows => rows.Count > 0, ct);
        Assert.Single(told);
        // …and exactly the budget left the bell.
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => teams.Handed.Count(m => recipients.Contains(m.Recipient)))
            .Where(n => n >= AccessGrantMailBudget.MailPerWindow)
            .FirstAsync().Timeout(TestTimeouts.WriteConvergence).Await(ct);
        Assert.Equal(AccessGrantMailBudget.MailPerWindow, teams.Handed.Count(m => recipients.Contains(m.Recipient)));
        Assert.DoesNotContain(teams.Handed, m => m.Recipient == granter);
    }

    [Fact(Timeout = 60000)]
    public async Task ABellOnlyRequest_NeverReachesTeamsOrEmail_WhateverThePreference()
    {
        var ct = TestContext.Current.CancellationToken;
        const string recipient = "bell_only_recipient";
        using (Access.ImpersonateAsSystem())
            await MeshService.CreateNode(new MeshNode(recipient)
            {
                NodeType = "User",
                Name = recipient,
                Content = new User { Email = $"{recipient}@acme.com", FullName = recipient },
            }).Should().Emit(cancellationToken: ct);

        var report = await NotificationService.Raise(Mesh, new NotificationRequest
            {
                Recipient = recipient,
                MainNodePath = recipient,
                TargetNodePath = "Parties",
                Title = LocalizableText.Verbatim("You've been given access to Parties"),
                Message = LocalizableText.Verbatim("You now have Viewer access to \"Parties\"."),
                Type = NotificationType.AccessGranted,
                CreatedBy = "granter",
                BellOnly = true,
            })
            .Timeout(TestTimeouts.WriteConvergence).Await(ct);

        // The default access-granted preference is bell + Teams (+ email): BellOnly leaves only the bell.
        var leg = Assert.Single(report);
        Assert.Equal(NotificationChannelKind.InApp, leg.Channel);
        Assert.True(leg.Delivered);
        Assert.DoesNotContain(teams.Handed, m => m.Recipient == recipient);
    }
}

/// <summary>The pure halves of the budget and of the notifier's use of it.</summary>
public class AccessGrantMailBudgetPureTest
{
    private static NotificationRequest Request() => new()
    {
        Recipient = "bob",
        MainNodePath = "bob",
        Title = LocalizableText.Verbatim("t"),
        Message = LocalizableText.Verbatim("m"),
        Type = NotificationType.AccessGranted,
    };

    [Fact]
    public void WithinBudget_TheRequestIsUnchanged_EmailAndTeamsStayOpen()
        => Assert.False(AccessGrantNotifier.Capped(Request(), AccessGrantMailVerdict.Mail).BellOnly);

    [Theory]
    [InlineData(AccessGrantMailVerdict.BellOnly)]
    [InlineData(AccessGrantMailVerdict.BellOnlyAndTellGranter)]
    public void OverBudget_TheRequestIsCappedToTheBell(AccessGrantMailVerdict verdict)
        => Assert.True(AccessGrantNotifier.Capped(Request(), verdict).BellOnly);

    [Fact]
    public void OnlyTheFirstOverBudgetGrant_TellsAPersonGranter()
    {
        Assert.True(AccessGrantNotifier.TellsGranter(AccessGrantMailVerdict.BellOnlyAndTellGranter, "alice"));
        Assert.False(AccessGrantNotifier.TellsGranter(AccessGrantMailVerdict.BellOnly, "alice"));
        Assert.False(AccessGrantNotifier.TellsGranter(AccessGrantMailVerdict.Mail, "alice"));
        // Nobody to tell: an unattributed grant, or one the system wrote.
        Assert.False(AccessGrantNotifier.TellsGranter(AccessGrantMailVerdict.BellOnlyAndTellGranter, null));
        Assert.False(AccessGrantNotifier.TellsGranter(AccessGrantMailVerdict.BellOnlyAndTellGranter, WellKnownUsers.System));
    }

    [Fact]
    public void TheWindowIsAFixedTenMinuteBucket()
    {
        Assert.Equal("202609291050", AccessGrantMailBudget.WindowKey(new DateTimeOffset(2026, 9, 29, 10, 50, 0, TimeSpan.Zero)));
        Assert.Equal("202609291050", AccessGrantMailBudget.WindowKey(new DateTimeOffset(2026, 9, 29, 10, 59, 59, TimeSpan.Zero)));
        Assert.Equal("202609291100", AccessGrantMailBudget.WindowKey(new DateTimeOffset(2026, 9, 29, 11, 0, 0, TimeSpan.Zero)));
        // UTC regardless of the offset the moment is expressed in.
        Assert.Equal("202609291050", AccessGrantMailBudget.WindowKey(new DateTimeOffset(2026, 9, 29, 12, 55, 0, TimeSpan.FromHours(2))));
    }

    [Theory]
    [InlineData("rbuergi", "rbuergi")]
    [InlineData("rbuergi@systemorph.com", "rbuergi@systemorph.com")]
    [InlineData("a/b c", "a_b_c")]
    [InlineData(".", "_")]
    [InlineData("..", "__")]
    [InlineData("a.b", "a.b")]
    [InlineData(null, "unknown")]
    [InlineData("  ", "unknown")]
    public void TheGranterIsOnePathSegment(string? granter, string expected)
        => Assert.Equal(expected, AccessGrantMailBudget.GranterKey(granter));
}
