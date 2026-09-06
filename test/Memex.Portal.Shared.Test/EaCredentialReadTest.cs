using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.AI;                       // IProviderKeyProtector, ProviderKeyProtector
using MeshWeaver.Graph.Configuration;      // AddEaCredentialType
using MeshWeaver.Fixture;                  // TestTimeouts.Convergence
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;            // IMasterKeyProvider
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #3433, both halves, on a real monolith mesh: the Executive Assistant's credential read must be
/// consumable from a HUB TURN, and a read that does not answer must not be reported as
/// <i>"this user never connected"</i>.
///
/// <para><b>What went wrong in production (2026-09-06).</b> <c>EaGraphAuth</c> was
/// <c>async</c>/<c>await</c>/<c>Task&lt;T&gt;</c> end to end and its credential read was
/// <c>await ws.GetMeshNodeStream(path).Take(1).Timeout(10s).FirstAsync()</c>. The class comment
/// justified that as "this sits at the OAuth/HTTP boundary (called from the consent controller and
/// the async EA tool)" — but the EA tool is not an HTTP boundary, it is an agent tool running on a
/// hub. Awaiting there parks the hub's turn, the reply to the read queues behind the parked turn,
/// the <c>Timeout</c> fires, and a blanket <c>catch</c> converted that into <c>(null, null)</c> —
/// the same value the method returns for a user who has no credential at all. A connected user was
/// handed the re-consent link.</para>
///
/// <para><b>The two tests that fail on the old code.</b>
/// <see cref="AwaitingTheCredentialReadInsideAHubTurn_NeverCompletes"/> pins the MECHANISM directly
/// against the mesh — no simulation, no mock — and
/// <see cref="AReadThatDoesNotAnswer_IsUndetermined_NotNotConnected"/> pins the consequence: the
/// failed read and the absent credential must not be the same value. Revert
/// <c>EaGraphAuth</c>/<c>IEaGraphAuth</c> with these unchanged and they collapse.</para>
/// </summary>
public class EaCredentialReadTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ConnectedUser = "ea-connected-user";
    private const string NeverConnectedUser = "ea-never-connected-user";

    /// <summary>An arbitrary, valid AES-256 key: the protector is real, only the key source is local.</summary>
    private sealed class FixedMasterKey : IMasterKeyProvider
    {
        private readonly byte[] key = new byte[32];
        public byte[]? GetMasterKey() => key;
    }

    /// <summary>The probe a hub turn answers — see the two hub-turn tests below.</summary>
    private record ReadCredentialFromTurn(string UserObjectId);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // The same registration ConfigureMemexMesh wires: the EaCredential NodeType plus its
            // mesh-wide content discriminator, so the node's content types on EVERY hub and not
            // only on its own (#2729 — without it ContentAs sees a raw JsonElement).
            .AddEaCredentialType()
            .AddMeshNodes(EaGraphAuth.NewCredentialNode(ConnectedUser) with
            {
                Content = new EaCredential
                {
                    UserObjectId = ConnectedUser,
                    // Stored form is irrelevant to the READ — GetConnection never unprotects.
                    RefreshTokenEncrypted = "enc:v1:seeded-for-the-read-test",
                    Scopes = EaGraphAuth.Scopes,
                    AcquiredAt = DateTimeOffset.UtcNow,
                }
            });

    /// <summary>
    /// The real class over the real mesh. <paramref name="readTimeout"/> is the ONLY thing a test
    /// varies — the way <c>ApiTokenService.ValidationReadTimeout</c> and <c>UserRoleResolver</c>'s
    /// <c>budget</c> are reachable — so the undetermined branch can be reached deterministically
    /// instead of waited for.
    /// </summary>
    private EaGraphAuth NewAuth(TimeSpan? readTimeout = null) => new(
        Mesh.ServiceProvider,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Microsoft:ClientId"] = "test-client",
            ["Authentication:Microsoft:ClientSecret"] = "test-secret",
        }).Build(),
        new ProviderKeyProtector(new FixedMasterKey()),
        // Never used: every test here exercises GetConnection, which reads the node and posts
        // nothing. A handler that throws would be reached only by a regression that started
        // calling the token endpoint from a pure connection check.
        new HttpClient(),
        Mesh.ServiceProvider.GetRequiredService<ILogger<EaGraphAuth>>())
    {
        CredentialReadTimeout = readTimeout ?? TimeSpan.FromSeconds(10),
    };

    // ── The mechanism ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE DEADLOCK, reproduced against the real mesh rather than simulated.
    ///
    /// <para>The hub's turn loop (<c>MessageService.DrainOne</c>) SUBSCRIBES to each turn and, for a
    /// turn that does not complete synchronously, <b>returns without dequeuing the next one</b> —
    /// the drain resumes only from that turn's terminal callback. So an <c>AsyncDelivery</c> handler
    /// that awaits holds the whole queue. The credential node lives on its own per-node hub, so the
    /// read's reply has to be processed by THIS hub — the one whose only turn is parked waiting for
    /// it. It cannot arrive, and the read's own <c>Timeout</c> is the only thing that ends the wait.
    /// That is line 197 of the old <c>EaGraphAuth</c>, executed.</para>
    ///
    /// <para>The credential IS present (seeded above), so "not connected" here is never the truth —
    /// it is the answer the parked turn manufactures.</para>
    /// </summary>
    [Fact]
    public async Task AwaitingTheCredentialReadInsideAHubTurn_NeverCompletes()
    {
        var verdict = new AsyncSubject<string>();
        var readBudget = TimeSpan.FromSeconds(3);

        // The shape `await` produces, expressed in the only way the hub's handler surface allows.
        // The hub's own SyncDelivery/AsyncDelivery delegates are already IObservable-typed, so an
        // `async` handler will not even compile — that door was closed. What `await ea.…Async(…)`
        // did was reopen it from the OTHER side: it made the caller's work not finish until the
        // read finished, which is precisely a turn that holds the drain. That is what this
        // registers.
        using var handler = Mesh.Register<ReadCredentialFromTurn>((delivery, _) =>
            Mesh.GetMeshNodeStream(EaGraphAuth.PathFor(delivery.Message.UserObjectId))
                .Take(1)
                .Timeout(readBudget)
                .Select(node => node is null ? "no node" : "connected")
                .Catch((Exception ex) => Observable.Return(ex.GetType().Name))
                .Do(v => { verdict.OnNext(v); verdict.OnCompleted(); })
                .Select(_ => delivery.Processed()));

        Mesh.Post(new ReadCredentialFromTurn(ConnectedUser), o => o.WithTarget(Mesh.Address));

        var answer = await verdict.Should().Within(TestTimeouts.Convergence).Emit();
        Output.WriteLine($"awaited-inside-the-turn verdict: {answer}");

        answer.Should().Be(nameof(TimeoutException),
            "the reply to a mesh read cannot be processed by a hub whose only turn is parked "
            + "awaiting that reply — the read times out even though the credential is present. "
            + "If this ever reports 'connected', the turn loop stopped being one-at-a-time and the "
            + "whole premise of the no-async rule needs re-measuring, not this test relaxing.");
    }

    /// <summary>
    /// The same read, same hub, same turn, same node — issued the sanctioned way. The handler
    /// composes the reactive seam and SUBSCRIBES, so the turn returns immediately, the hub is free
    /// to process the read's reply, and the answer arrives.
    ///
    /// <para>This is the control for the test above: it is what makes that one a statement about
    /// <c>await</c> rather than about the mesh being slow.</para>
    /// </summary>
    [Fact]
    public async Task SubscribingToTheReactiveSeamInsideAHubTurn_Answers()
    {
        var ea = NewAuth();
        var verdict = new AsyncSubject<EaGraphAccess>();

        using var handler = Mesh.Register<ReadCredentialFromTurn>(delivery =>
        {
            ea.GetConnection(delivery.Message.UserObjectId)
                .Subscribe(
                    access => { verdict.OnNext(access); verdict.OnCompleted(); },
                    ex => verdict.OnError(ex));
            return delivery.Processed();
        });

        Mesh.Post(new ReadCredentialFromTurn(ConnectedUser), o => o.WithTarget(Mesh.Address));

        var access = await verdict.Should().Within(TestTimeouts.Convergence).Emit();
        Output.WriteLine($"subscribed-from-the-turn verdict: {access}");

        access.Connection.Should().Be(EaConnection.Connected,
            "a hub turn that subscribes instead of awaiting leaves the hub free to process the "
            + "read's own reply, so the stored credential is found");
    }

    // ── Absent is not failed ─────────────────────────────────────────────────────────────────────

    /// <summary>A stored credential reads back as connected. The positive control for everything below.</summary>
    [Fact]
    public async Task AStoredCredential_ReadsAsConnected()
    {
        var access = await NewAuth().GetConnection(ConnectedUser)
            .Should().Within(TestTimeouts.Convergence).Emit();

        access.Connection.Should().Be(EaConnection.Connected);
        access.AccessToken.Should().BeNull("GetConnection reads the credential; it never mints a token");
    }

    /// <summary>
    /// A user with no credential node reads as <see cref="EaConnection.NotConnected"/> — the ONE
    /// state in which offering the consent link is truthful. Without this the fix could "pass" by
    /// answering <see cref="EaConnection.Undetermined"/> to everything, which would strand every
    /// genuinely-new user.
    /// </summary>
    [Fact]
    public async Task AUserWithNoCredential_ReadsAsNotConnected()
    {
        var access = await NewAuth().GetConnection(NeverConnectedUser)
            .Should().Within(TestTimeouts.Convergence).Emit();

        Output.WriteLine($"absent-credential verdict: {access}");
        access.Connection.Should().Be(EaConnection.NotConnected,
            "an absent credential is a completed read with a negative answer, and the consent link "
            + "is the correct response to it");
    }

    /// <summary>
    /// 🚨 THE COLLAPSE, pinned. A read that does not answer — here the same read as
    /// <see cref="AStoredCredential_ReadsAsConnected"/>, given a budget it cannot meet — must be
    /// <see cref="EaConnection.Undetermined"/>, and must therefore differ from the answer for a user
    /// who genuinely has no credential.
    ///
    /// <para>The two assertions are deliberately separate. The first says what the failed read IS;
    /// the second says what it is NOT. On the pre-fix code both sites returned the same
    /// <c>(null, null)</c> — <c>GetAccessTokenAsync</c> then answered <c>null</c> and the EA showed
    /// the re-consent link — so BOTH fail, and the second is the one that names the user-visible
    /// defect.</para>
    ///
    /// <para>The budget is a test-only override, not a production knob: #3433 says explicitly that
    /// widening the timeout is not the fix. It is used here in the shrinking direction, to reach the
    /// branch deterministically instead of racing it.</para>
    /// </summary>
    [Fact]
    public async Task AReadThatDoesNotAnswer_IsUndetermined_NotNotConnected()
    {
        // A budget no cross-hub node read can meet. Paired with AStoredCredential_ReadsAsConnected
        // above, which reads the SAME node with the production budget and finds it — so this is a
        // statement about the failed read, not about the node being missing.
        var failed = await NewAuth(TimeSpan.FromTicks(1)).GetConnection(ConnectedUser)
            .Should().Within(TestTimeouts.Convergence).Emit();
        var absent = await NewAuth().GetConnection(NeverConnectedUser)
            .Should().Within(TestTimeouts.Convergence).Emit();

        Output.WriteLine($"failed-read verdict: {failed}");
        Output.WriteLine($"absent-credential verdict: {absent}");

        failed.Connection.Should().Be(EaConnection.Undetermined,
            "a read that did not complete tells us nothing about the user's connection state");
        failed.Diagnostic.Should().NotBeNullOrEmpty(
            "an undetermined answer with nothing to say is indistinguishable from the swallow this "
            + "fix removes — the bare `catch { return null; }` logged nothing at all");

        failed.Connection.Should().NotBe(absent.Connection,
            "THIS is #3433: the failed read and the absent credential used to be the same value, so "
            + "a user whose grant was stored and valid was told to connect their mailbox again");
    }

    /// <summary>
    /// The seam is reactive, and stays reactive. A <c>Task</c>-returning member on
    /// <see cref="IEaGraphAuth"/> is what FORCED the await at the hub-side call site — a caller
    /// cannot consume a <c>Task&lt;T&gt;</c> any other way — so the three reactive entry points are
    /// pinned here by reflection rather than trusted to review.
    ///
    /// <para>The three retiring <c>…Async</c> forwarders are deliberately NOT asserted absent: they
    /// exist for the duration of the MeshWeaver.Plugins migration and are counted as debt by
    /// <c>HubReachableAsyncGuard</c>'s contract-seam arm, which is what removes them. What this
    /// asserts is that the REACTIVE surface exists and is the primary one.</para>
    /// </summary>
    [Fact]
    public void TheSeamsPrimarySurfaceIsReactive()
    {
        foreach (var name in new[]
                 {
                     nameof(IEaGraphAuth.GetConnection),
                     nameof(IEaGraphAuth.GetAccessToken),
                     nameof(IEaGraphAuth.ExchangeAndStore),
                 })
        {
            var method = typeof(IEaGraphAuth).GetMethod(name);
            method.Should().NotBeNull($"{name} is the hub-facing entry point");
            method!.ReturnType.IsGenericType.Should().BeTrue();
            method.ReturnType.GetGenericTypeDefinition().Should().Be(typeof(IObservable<>),
                $"{name} is consumed from an agent round on a hub, where a Task can only be "
                + "consumed by awaiting and awaiting parks the turn (#3433)");
        }
    }
}
