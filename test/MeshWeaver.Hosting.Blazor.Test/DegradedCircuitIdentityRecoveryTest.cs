using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// Pins the repair for a Blazor circuit that opened while the mesh user index could not answer —
/// the point where "the index cannot answer yet" must stop being latched as the circuit's identity.
///
/// <para><b>The defect.</b> <c>CircuitAccessHandler</c> resolves the circuit user exactly once, on
/// circuit open. When <c>UserIdentityCache.Lookup</c> came back UNAVAILABLE it fell back to the
/// email local part for the partition key and — because <c>TimeZoneId</c> and <c>Locale</c> are read
/// off the mesh <c>User</c> node's profile — to UTC and English. Nothing ever re-asked. A user who
/// signed in during a portal restart, a slow first query or a storage stall therefore got a degraded
/// identity and an ENGLISH portal for the entire life of the circuit, healed only by reloading the
/// page. That defeats the reason locale is resolved explicitly off <c>AccessContext.Locale</c> in
/// the first place.</para>
///
/// <para><b>The shape of the fix</b> (the same one #1085 applied to the indeterminate NodeType
/// probe): an unavailable lookup is a PENDING QUESTION, and the thing being waited on is already an
/// observable event — the index is filled by a live <c>nodeType:User</c> query. So the degraded leg
/// falls through to that same emission and re-asks. No timer, no poll, no retry loop, no widened
/// timeout: those only narrow the window in which the bug shows.</para>
///
/// <para><b>Why these are pure.</b> <c>Circuit</c> cannot be constructed by a test (no public
/// constructor), so the handler's own entry point is unreachable; and the interesting window — the
/// index unavailable — is precisely the window a live-mesh test cannot hold open. Both halves of the
/// production path are therefore exercised directly: the wait
/// (<see cref="UserIdentityLookup.UntilDetermined"/>, driven entirely by a virtual-time scheduler)
/// and the projection (<see cref="CircuitAccessHandler.ApplyMeshUser"/>, a pure function of seed and
/// node). Same reason <c>UserIdentityLookupClassifierTest</c> is dependency-free.</para>
/// </summary>
public class DegradedCircuitIdentityRecoveryTest : ReactiveTest
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The mesh User node a German viewer with a Zurich profile has.</summary>
    private static MeshNode GermanUser(string id = "rbuergi") =>
        new(id)
        {
            Name = "Roland Bürgi",
            Content = new User
            {
                Email = "rbuergi@systemorph.com",
                Locale = "de",
                TimeZoneId = "Europe/Zurich"
            }
        };

    /// <summary>What <c>CircuitAccessHandler</c> seeds when the index cannot answer: partition key
    /// guessed from the email local part, and NO locale / time zone — i.e. English and UTC.</summary>
    private static AccessContext DegradedSeed() =>
        new()
        {
            ObjectId = "rbuergi",
            Name = "Roland Bürgi",
            Email = "rbuergi@systemorph.com"
        };

    // ---- The wait: an unavailable index is not a verdict ----

    [Fact]
    public void UnavailableIndex_EmitsNothing_ItIsNotAVerdict()
    {
        var scheduler = new TestScheduler();
        // The index never fills within the test's horizon: every re-ask is still unavailable.
        var indexChanged = scheduler.CreateHotObservable(
            OnNext(200, Unit.Default),
            OnNext(400, Unit.Default));

        var observer = scheduler.CreateObserver<UserIdentityLookup>();
        UserIdentityLookup
            .UntilDetermined(indexChanged, () => UserIdentityLookup.Unavailable("cold index"))
            .Subscribe(observer);

        scheduler.Start();

        // 🚨 The whole point: no emission at all. Nothing downstream can mistake "cannot answer"
        // for an answer, because there is nothing to mistake.
        observer.Messages.Should().BeEmpty();
    }

    [Fact]
    public void WhenTheIndexFills_TheAnswerArrives_OnTheIndexsOwnEmission()
    {
        var scheduler = new TestScheduler();
        var node = GermanUser();
        // Hydrates between the two index emissions — i.e. LONG after the circuit opened.
        var indexChanged = scheduler.CreateHotObservable(
            OnNext(200, Unit.Default),
            OnNext(400, Unit.Default));
        UserIdentityLookup Lookup() => scheduler.Clock < 300
            ? UserIdentityLookup.Unavailable("cold index")
            : UserIdentityLookup.Found(node);

        var observer = scheduler.CreateObserver<UserIdentityLookup>();
        UserIdentityLookup.UntilDetermined(indexChanged, Lookup).Subscribe(observer);

        scheduler.Start();

        // Exactly one answer, and it arrives ON the index emission that could produce it (400) —
        // not on a timer, not on a poll interval, not at some fixed ceiling.
        observer.Messages.Should().HaveCount(2, "one verdict, then completion");
        observer.Messages[0].Time.Should().Be(400);
        observer.Messages[0].Value.Value.Node.Should().BeSameAs(node);
        observer.Messages[1].Value.Kind.Should().Be(NotificationKind.OnCompleted);
    }

    [Fact]
    public void AlreadyDetermined_AtSubscribeTime_IsNotMissed()
    {
        // The sample-then-subscribe race: the caller reaches the repair BECAUSE its own Lookup came
        // back unavailable, and the snapshot can land in the gap before this subscription exists.
        // Without the re-ask at subscribe time that emission is gone and the circuit waits forever
        // on an index that has already answered.
        var scheduler = new TestScheduler();
        var node = GermanUser();
        // Never fires again — the ONLY way to get an answer is to ask at subscribe time.
        var indexChanged = scheduler.CreateHotObservable<Unit>();

        var observer = scheduler.CreateObserver<UserIdentityLookup>();
        UserIdentityLookup
            .UntilDetermined(indexChanged, () => UserIdentityLookup.Found(node))
            .Subscribe(observer);

        scheduler.Start();

        observer.Messages.Should().HaveCount(2);
        observer.Messages[0].Value.Value.Node.Should().BeSameAs(node);
    }

    [Fact]
    public void ADefinitiveUnknown_EndsTheWait()
    {
        // "No mesh User node carries this email" IS an answer, and the seeded identity is the right
        // response to it. Continuing to listen would leave a live subscription per never-onboarded
        // visitor — the per-circuit accumulation that wedges a portal.
        var scheduler = new TestScheduler();
        var indexChanged = scheduler.CreateHotObservable(OnNext(200, Unit.Default));

        var observer = scheduler.CreateObserver<UserIdentityLookup>();
        UserIdentityLookup
            .UntilDetermined(indexChanged, () => UserIdentityLookup.Unknown)
            .Subscribe(observer);

        scheduler.Start();

        observer.Messages.Should().HaveCount(2);
        observer.Messages[0].Value.Value.Node.Should().BeNull();
        observer.Messages[1].Value.Kind.Should().Be(NotificationKind.OnCompleted);
    }

    // ---- The projection: the repair applies the SAME upgrade the success path applies ----

    [Fact]
    public void ApplyMeshUser_AdoptsTheProfilesLanguageAndTimeZone()
    {
        // 🚨 The user-visible half of the defect. The seed carries no Locale, which renders English.
        var upgraded = CircuitAccessHandler.ApplyMeshUser(DegradedSeed(), GermanUser(), JsonOptions);

        upgraded.Locale.Should().Be("de");
        upgraded.TimeZoneId.Should().Be("Europe/Zurich");
        upgraded.ObjectId.Should().Be("rbuergi");
        upgraded.Name.Should().Be("Roland Bürgi");
    }

    [Fact]
    public void ApplyMeshUser_KeepsTheSeedWhereTheProfileSaysNothing()
    {
        // An unset profile field must not overwrite the seed with null — "never signed in on a
        // browser yet" means fall back, not erase.
        var seed = DegradedSeed() with { Locale = "fr", TimeZoneId = "Europe/Paris" };
        var node = new MeshNode("rbuergi") { Name = "Roland", Content = new User() };

        var upgraded = CircuitAccessHandler.ApplyMeshUser(seed, node, JsonOptions);

        upgraded.Locale.Should().Be("fr");
        upgraded.TimeZoneId.Should().Be("Europe/Paris");
    }

    // ---- The two halves composed: the repair a degraded circuit actually performs ----

    [Fact]
    public void ADegradedCircuit_ReResolvesToTheViewersOwnLanguage()
    {
        // End to end over the production pieces: circuit opens on the seed while the index is cold,
        // the index fills later, and the circuit's identity is upgraded off the index's own
        // emission. Before the fix this test's subject did not exist at all — an unavailable lookup
        // produced the seed and nothing ever asked again, so `published` stayed the degraded seed
        // (Locale null ⇒ English) for the circuit's whole life.
        var scheduler = new TestScheduler();
        var node = GermanUser();
        var seed = DegradedSeed();
        var indexChanged = scheduler.CreateHotObservable(OnNext(300, Unit.Default));
        UserIdentityLookup Lookup() => scheduler.Clock < 300
            ? UserIdentityLookup.Unavailable("the mesh user index has not received its first snapshot yet")
            : UserIdentityLookup.Found(node);

        // What CircuitAccessHandler.StartIdentityRecovery composes, and what it publishes through
        // the existing UpdateUserContext hook.
        AccessContext published = seed;
        UserIdentityLookup.UntilDetermined(indexChanged, Lookup)
            .Where(l => l.Node is not null)
            .Select(l => CircuitAccessHandler.ApplyMeshUser(seed, l.Node!, JsonOptions))
            .Subscribe(ctx => published = ctx);

        // The circuit is live and degraded before the index answers.
        published.Locale.Should().BeNull("the circuit opened before the index could answer");

        scheduler.Start();

        published.Locale.Should().Be("de", "the viewer's own language, not the degraded default");
        published.TimeZoneId.Should().Be("Europe/Zurich");
    }
}
