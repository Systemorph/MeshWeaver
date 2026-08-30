using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Logon;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The logon-action framework's semantics: run-once vs every-logon, the durable per-user ledger, and
/// what happens when two logons race.
///
/// <para>🚨 Every assertion here is on OBSERVABLE STATE — the user's own profile — never on a log
/// line or an invocation count held only in the test. The actions used are real
/// <see cref="ILogonAction"/> implementations registered in the mesh exactly as a platform action
/// is; nothing about <c>IMessageHub</c> or <c>IMeshService</c> is mocked.</para>
///
/// <para>The marker trick that makes "ran twice" VISIBLE: each run captures its own sequence number
/// and the profile change appends <c>ran-{n}</c>. The runner may re-invoke the profile lambda when
/// the owning hub rebases a stale patch, and that re-invocation produces the SAME marker — so the
/// pins show how many runs were COMMITTED, which is the thing under test, rather than how many times
/// a lambda happened to be evaluated.</para>
/// </summary>
public class LogonActionFrameworkTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string UserPath = "logonuser";

    private readonly MarkerLogonAction _once = new("test.once", LogonActionMode.RunOnce);
    private readonly MarkerLogonAction _every = new("test.every", LogonActionMode.EveryLogon);
    private readonly FailingLogonAction _failing = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                .AddSingleton<ILogonAction>(_once)
                .AddSingleton<ILogonAction>(_every)
                .AddSingleton<ILogonAction>(_failing));

    [Fact(Timeout = 60000)]
    public async Task A_run_once_action_runs_on_the_first_logon_and_not_on_the_second()
    {
        await CreateUserAsync(UserPath);
        var identity = IdentityFor(UserPath);
        var runner = Mesh.ServiceProvider.GetRequiredService<LogonActionRunner>();

        await runner.RunFor(identity).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await();
        var afterFirst = await AwaitProfileAsync(UserPath, u => u.CompletedLogonActions.ContainsKey(_once.Id));

        afterFirst.PinnedPaths.Should().ContainSingle(p => p.StartsWith("once-"),
            "the run-once action committed exactly one marker");
        afterFirst.CompletedLogonActions.Keys.Should().Contain(_once.Id,
            "the ledger entry lands in the SAME patch as the effect");

        // Second logon: the ledger key is present, so the action is not even prepared.
        await runner.RunFor(identity).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await();
        // Wait on a POSITIVE signal from the second run — the every-logon action's second marker —
        // so this is not a "wait and hope nothing happened" assertion.
        var afterSecond = await AwaitProfileAsync(
            UserPath, u => u.PinnedPaths.Count(p => p.StartsWith("every-")) >= 2);

        afterSecond.PinnedPaths.Count(p => p.StartsWith("once-")).Should().Be(1,
            "a run-once action must never apply a second time");
    }

    [Fact(Timeout = 60000)]
    public async Task An_every_logon_action_runs_on_every_logon()
    {
        await CreateUserAsync(UserPath);
        var identity = IdentityFor(UserPath);
        var runner = Mesh.ServiceProvider.GetRequiredService<LogonActionRunner>();

        await runner.RunFor(identity).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await();
        await AwaitProfileAsync(UserPath, u => u.PinnedPaths.Any(p => p.StartsWith("every-")));

        await runner.RunFor(identity).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await();
        var profile = await AwaitProfileAsync(
            UserPath, u => u.PinnedPaths.Count(p => p.StartsWith("every-")) >= 2);

        profile.PinnedPaths.Count(p => p.StartsWith("every-")).Should().Be(2);
        profile.CompletedLogonActions.Keys.Should().NotContain(_every.Id,
            "an every-logon action is deliberately NOT recorded — the ledger is what would stop it");
    }

    [Fact(Timeout = 60000)]
    public async Task Two_concurrent_logons_run_a_once_action_exactly_once()
    {
        await CreateUserAsync(UserPath);
        var identity = IdentityFor(UserPath);
        var runner = Mesh.ServiceProvider.GetRequiredService<LogonActionRunner>();

        // A REAL race: both pipelines are subscribed before either can complete, so both observe an
        // empty ledger in their fast-path check and both reach the commit. What separates them is
        // the owning hub serialising the two patches and the second lambda re-reading the ledger key
        // the first one wrote — the guard this test exists for.
        var first = runner.RunFor(identity).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await();
        var second = runner.RunFor(identity).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await();
        await Task.WhenAll(first, second);

        var profile = await AwaitProfileAsync(UserPath, u => u.CompletedLogonActions.ContainsKey(_once.Id));

        profile.PinnedPaths.Count(p => p.StartsWith("once-")).Should().Be(1,
            "two concurrent logons must commit the run-once effect exactly once");
    }

    [Fact(Timeout = 60000)]
    public async Task A_user_who_already_ran_the_action_does_not_re_run_it_after_a_restart()
    {
        // The ledger is DURABLE — part of the profile, not process state. A user whose profile
        // already carries the key is indistinguishable from one who ran it before this process
        // started, which is exactly the post-restart case.
        const string path = "logonuser-restarted";
        await CreateUserAsync(path, new User
        {
            PinnedPaths = ["kept/by/the/user"],
            CompletedLogonActions = new Dictionary<string, DateTimeOffset>
            {
                [_once.Id] = DateTimeOffset.UtcNow.AddDays(-3),
            },
        });

        var runner = Mesh.ServiceProvider.GetRequiredService<LogonActionRunner>();
        await runner.RunFor(IdentityFor(path)).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await();

        // Positive signal: the EVERY-logon action still ran, which proves the runner executed and
        // the absence of a once-marker is a decision rather than a no-op run.
        var profile = await AwaitProfileAsync(path, u => u.PinnedPaths.Any(p => p.StartsWith("every-")));

        profile.PinnedPaths.Should().NotContain(p => p.StartsWith("once-"),
            "the durable ledger survives a restart, so the migration does not re-apply");
        profile.PinnedPaths.Should().Contain("kept/by/the/user");
    }

    [Fact(Timeout = 60000)]
    public async Task A_failing_action_is_not_recorded_and_does_not_stop_the_others()
    {
        await CreateUserAsync(UserPath);
        var runner = Mesh.ServiceProvider.GetRequiredService<LogonActionRunner>();

        await runner.RunFor(IdentityFor(UserPath)).FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await();
        var profile = await AwaitProfileAsync(UserPath, u => u.CompletedLogonActions.ContainsKey(_once.Id));

        profile.CompletedLogonActions.Keys.Should().NotContain(_failing.Id,
            "recording a failed action would silently skip it forever; it must be retried next logon");
        profile.PinnedPaths.Should().Contain(p => p.StartsWith("once-"),
            "one action's failure must not take the rest of the run down with it");
    }

    [Fact(Timeout = 60000)]
    public async Task An_anonymous_visitor_runs_no_logon_actions()
    {
        // Anonymous is a perfectly non-empty string and a partition nobody's profile lives in.
        // Running migrations for it would write a User node for a visitor.
        var runner = Mesh.ServiceProvider.GetRequiredService<LogonActionRunner>();
        var before = _every.Runs;

        await runner.RunFor(new AccessContext { ObjectId = WellKnownUsers.Anonymous, Name = "Anonymous" })
            .FirstAsync().Timeout(TimeSpan.FromSeconds(15)).Await();

        _every.Runs.Should().Be(before, "an unauthenticated caller is not a logon");
    }

    // ---------------------------------------------------------------- helpers

    private static AccessContext IdentityFor(string userPath) =>
        new() { ObjectId = userPath, Name = userPath, Email = $"{userPath}@meshweaver.io" };

    /// <summary>
    /// Creates the user's partition-root node exactly as onboarding does — as System, because
    /// UserNodeType's access rule reserves creating a User node to the platform, and the user being
    /// onboarded does not exist yet to hold a grant on themselves. RunAsSystem, never
    /// Observable.Using(ImpersonateAsSystem): the latter opens and closes the AsyncLocal scope on
    /// different threads (#1790).
    /// </summary>
    private async Task CreateUserAsync(string path, User? content = null)
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetService<AccessService>();
        await access.RunAsSystem(() => mesh.CreateNode(MeshNode.FromPath(path) with
        {
            NodeType = "User",
            Name = path,
            State = MeshNodeState.Active,
            Content = content ?? new User { FullName = path },
        })).FirstAsync().Timeout(TimeSpan.FromSeconds(20)).Await();
    }

    /// <summary>
    /// Waits on the CONDITION via the node stream — never a Task.Delay, which would race CI load in
    /// both directions.
    /// </summary>
    private async Task<User> AwaitProfileAsync(string path, Func<User, bool> predicate)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n?.ContentAs<User>(Mesh.JsonSerializerOptions) is { } u && predicate(u))
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .Await();
        return node.ContentAs<User>(Mesh.JsonSerializerOptions)!;
    }

    /// <summary>
    /// A real logon action whose effect is deliberately NOT idempotent, so a second commit is
    /// visible in the profile. Registered as a mesh singleton, so its counter dies with the mesh —
    /// no static state, nothing to Clear() between tests.
    /// </summary>
    private sealed class MarkerLogonAction(string id, LogonActionMode mode) : ILogonAction
    {
        private int _runs;

        public string Id => id;
        public LogonActionMode Mode => mode;
        public int Order => mode == LogonActionMode.RunOnce ? 0 : 10;

        /// <summary>How many times <see cref="Run"/> was entered — used only to assert that a run
        /// did NOT happen at all (the anonymous case); every other assertion reads the profile.</summary>
        public int Runs => Volatile.Read(ref _runs);

        public IObservable<LogonActionOutcome> Run(LogonActionContext context)
        {
            var n = Interlocked.Increment(ref _runs);
            var marker = $"{(mode == LogonActionMode.RunOnce ? "once" : "every")}-{n}";
            // The marker is captured HERE, per run — so a rebase re-invoking the lambda yields the
            // same marker, and only a genuinely separate run yields a new one.
            return Observable.Return(LogonActionOutcome.Profile(user =>
                user.PinnedPaths.Contains(marker)
                    ? user
                    : user with { PinnedPaths = [.. user.PinnedPaths, marker] }));
        }
    }

    /// <summary>An action that always faults — the framework must log it, skip it, leave it
    /// unrecorded, and carry on with the rest.</summary>
    private sealed class FailingLogonAction : ILogonAction
    {
        public string Id => "test.failing";
        public LogonActionMode Mode => LogonActionMode.RunOnce;
        public int Order => 5;

        public IObservable<LogonActionOutcome> Run(LogonActionContext context) =>
            Observable.Throw<LogonActionOutcome>(new InvalidOperationException("deliberate"));
    }
}
