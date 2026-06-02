using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using NSubstitute;
using Xunit;
using NavigationContext = MeshWeaver.Mesh.Services.NavigationContext;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// Tests for the <see cref="NavigationService.Status"/> observable pipeline.
///
/// These enforce the "no endless spinner" contract: every phase of the page-lookup
/// pipeline surfaces a descriptive, non-empty message, and the "Page Not Found"
/// card never flashes during an in-progress retry loop.
///
/// The tests use short retry delays via the internal test-only constructor so the
/// retry-exhaustion path runs in milliseconds rather than ~11.5 s of production
/// backoff.
/// </summary>
public class NavigationProgressTest
{
    private readonly MockNavigationManager _navigationManager;
    private readonly IPathResolver _pathResolver;
    private readonly IMeshQueryCore _meshQuery;
    private readonly IMessageHub _hub;
    private readonly IServiceProvider _hubServiceProvider;
    private readonly ICreatableTypesProvider _creatableTypesProvider;

    public NavigationProgressTest()
    {
        _navigationManager = new MockNavigationManager();
        _pathResolver = Substitute.For<IPathResolver>();
        _meshQuery = Substitute.For<IMeshQueryCore>();
        _hub = Substitute.For<IMessageHub>();
        _hubServiceProvider = Substitute.For<IServiceProvider>();
        _creatableTypesProvider = Substitute.For<ICreatableTypesProvider>();

        _hub.ServiceProvider.Returns(_hubServiceProvider);
        _hubServiceProvider.GetService(typeof(ICreatableTypesProvider))
            .Returns(_creatableTypesProvider);
        // IMeshQueryCore is resolved through hub.ServiceProvider.GetRequiredService
        // (the lazy pattern that VUserHelper / SyncedQueryMeshNodes also use), so
        // wire the substitute through the hub's service provider rather than
        // injecting it into the constructor.
        _hubServiceProvider.GetService(typeof(IMeshQueryCore)).Returns(_meshQuery);

        // Empty mesh query by default â€” node-loading path is best-effort.
        // Empty observable so the chain in LoadNodeWithPreRenderedHtml completes
        // with node = null (Catch falls through to Observable.Return(null)).
        _meshQuery.Query<MeshNode>(Arg.Any<MeshQueryRequest>(), Arg.Any<JsonSerializerOptions>())
            .Returns(System.Reactive.Linq.Observable.Empty<QueryResultChange<MeshNode>>());
    }

    // Short retries so retry-exhaustion tests run in under ~100 ms total.
    private static readonly int[] FastRetryDelays = [10, 10, 10];

    // Stream-wait timeout for the reactive assertions below — preserves the 15 s
    // budget the previous Task-bridging helpers used so a real pipeline hang
    // surfaces with a real exception instead of a stale-emissions assertion.
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    private NavigationService CreateService(int[]? retryDelays = null) =>
        new(_navigationManager, _pathResolver, _hub, retryDelays ?? FastRetryDelays);

    private static List<NavigationStatus> CaptureStatus(NavigationService service)
    {
        var list = new List<NavigationStatus>();
        service.Status.Subscribe(list.Add);
        return list;
    }

    // -- Test #1: initial subscribers see a non-empty LookingUp message. ----------

    [Fact]
    public void Status_AfterInitialize_EmitsNonEmptyLookingUpMessageWithPath()
    {
        // After InitializeAsync runs, subscribers must see "Looking up <path>"
        // rather than a silent / generic spinner. The constructor deliberately
        // emits LookingUp(null) — reading NavigationManager.Uri at DI
        // construction time throws "RemoteNavigationManager has not been
        // initialized" first-chance every circuit start. The path lands the
        // moment InitializeAsync runs (from a safe component lifecycle).
        _navigationManager.SetUri("http://localhost/FutuRe/EuropeRe");
        // Stub a never-resolving path resolver so the LookingUp emission
        // sticks (otherwise the resolution would race and Status would flip
        // to Redirecting / NotFound before the assertion).
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Subjects.Subject.Synchronize(
                new System.Reactive.Subjects.Subject<AddressResolution?>()));

        var service = CreateService();

        service.InitializeAsync();
        // Stream-wait for the LookingUp emission carrying the path — replaces
        // a 50 ms propagation delay. The Status stream is a BehaviorSubject,
        // so the .Where filter is hot on first match.
        var lookingUp = service.Status.Should().Within(WaitTimeout)
            .Match(s => s.Phase == NavigationPhase.LookingUp && s.Message.Contains("FutuRe"));

        lookingUp.Should().NotBeNull("after InitializeAsync the LookingUp emission must carry the path");
        lookingUp!.Message.Should().NotBeNullOrWhiteSpace("no spinner without a descriptive label");
        lookingUp.Message.Should().Contain("FutuRe/EuropeRe",
            "the current path should be named in the LookingUp status");
    }

    // -- Test #2: during initial resolution the status says "Looking up <path>". --

    [Fact]
    public void Status_DuringInitialResolution_EmitsLookingUpWithPath()
    {
        _navigationManager.SetUri("http://localhost/ACME/Project");
        // AsyncSubject — single emission then complete; fully reactive (no Task bridge).
        var resolveSubject = new System.Reactive.Subjects.AsyncSubject<AddressResolution?>();
        _pathResolver.ResolvePath(Arg.Any<string>()).Returns(resolveSubject);

        var service = CreateService();
        var emissions = CaptureStatus(service);
        // InitializeAsync subscribes to ResolvePath synchronously, so the
        // LookingUp status is published before the call returns — no Task
        // coordination needed to observe it.
        service.InitializeAsync();

        // Assert the user would see a "Looking up" message right now.
        emissions.Should().Contain(s => s.Phase == NavigationPhase.LookingUp
                                        && s.Message.Contains("ACME/Project"),
            "user must see what's being looked up, not a blank spinner");

        // Complete the resolution so the per-path subscription doesn't dangle.
        resolveSubject.OnNext(new AddressResolution("ACME/Project", null));
        resolveSubject.OnCompleted();
    }

    // -- Test #3: after successful resolution, Redirecting with address. ----------

    [Fact]
    public void Status_AfterSuccessfulResolution_EmitsRedirectingWithAddress()
    {
        _navigationManager.SetUri("http://localhost/ACME/Project");
        _pathResolver.ResolvePath("ACME/Project")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));

        var service = CreateService();
        var emissions = CaptureStatus(service);

        // The Status pipeline runs Looking→Redirecting→Loading synchronously off
        // the bootstrap (ResolvePath returns Observable.Return), so by the time
        // InitializeAsync returns the `emissions` accumulator (subscribed before
        // the call) has already captured the Redirecting emission.
        service.InitializeAsync();

        emissions.Should().Contain(s => s.Phase == NavigationPhase.Redirecting
                                        && s.Message.Contains("ACME/Project"),
            "resolved path should surface 'Redirecting to <address>' to the user");
    }

    // -- Test #4: Redirecting message includes area when non-empty. ---------------

    [Fact]
    public void Status_AfterSuccessfulResolution_WithArea_IncludesAreaInMessage()
    {
        _navigationManager.SetUri("http://localhost/ACME/Project/Dashboard");
        _pathResolver.ResolvePath("ACME/Project/Dashboard")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", "Dashboard")));

        var service = CreateService();
        var emissions = CaptureStatus(service);

        // Redirecting lands synchronously off the bootstrap; the `emissions`
        // accumulator captured it before InitializeAsync returned.
        service.InitializeAsync();

        emissions.Should().Contain(s => s.Phase == NavigationPhase.Redirecting
                                        && s.Message.Contains("area Dashboard"));
    }

    // -- Test #5: Redirecting message omits "area" when area is null. -------------

    [Fact]
    public void Status_AfterSuccessfulResolution_WithNoArea_OmitsAreaSuffix()
    {
        _navigationManager.SetUri("http://localhost/ACME/Project");
        _pathResolver.ResolvePath("ACME/Project")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));

        var service = CreateService();
        var emissions = CaptureStatus(service);

        // Redirecting lands synchronously off the bootstrap; the `emissions`
        // accumulator captured it before InitializeAsync returned.
        service.InitializeAsync();

        var redirecting = emissions.FirstOrDefault(s => s.Phase == NavigationPhase.Redirecting);
        redirecting.Should().NotBeNull();
        redirecting!.Message.Should().NotContain("area",
            "area segment must not appear when the resolved path has no area remainder");
    }

    // -- Test #6: THE core "no endless spinner" invariant --------------------------

    [Fact]
    public void Status_AllEmissions_HaveNonEmptyMessage()
    {
        // Drive the service through a full lifecycle: init â†’ resolve ok â†’ navigate
        // to a path that will NOT resolve â†’ retries â†’ NotFound. Every emission
        // along the way must carry a non-empty message.
        _navigationManager.SetUri("http://localhost/ACME/Project");
        _pathResolver.ResolvePath("ACME/Project")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));
        _pathResolver.ResolvePath("does/not/exist")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(null));

        var service = CreateService();
        var emissions = CaptureStatus(service);

        service.InitializeAsync();
        _navigationManager.SimulateLocationChanged("http://localhost/does/not/exist");
        // Stream-wait for the NotFound emission — replaces a Task.Delay(200)
        // "wait > total FastRetryDelays" barrier. NotFound is the terminal
        // emission for the failed retry path (fired by the watchdog timer);
        // once we see it, the lifecycle is over and we can assert on `emissions`.
        service.Status.Should().Within(WaitTimeout)
            .Match(s => s.Phase == NavigationPhase.NotFound);

        emissions.Should().NotBeEmpty();
        emissions.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.Message),
            "no emission â€” including intermediate ones â€” may render as an empty spinner");
    }

    // -- Test #7: retries in flight must NOT emit NotFound / null context --------

    [Fact]
    public void Status_WhenResolutionFailsInitially_DoesNotEmitNotFoundUntilRetriesExhausted()
    {
        // Initial attempt returns null and we schedule retries. The user should
        // keep seeing "Looking upâ€¦" during the retry window â€” not a flash of
        // "Page Not Found" followed by the real answer.
        _navigationManager.SetUri("http://localhost/does/not/exist");
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(null));

        // Extra-long retries to widen the window we inspect.
        var service = CreateService(retryDelays: [500, 500, 500]);
        var emissions = CaptureStatus(service);

        service.InitializeAsync();

        // During the retry window (< first 500 ms retry) the watchdog has not
        // fired, so neither a NotFound status nor a null navigation context may
        // surface. These are genuine "nothing happens" negatives — the one place
        // a fixed wait is correct (NotEmit).
        service.Status.Where(s => s.Phase == NavigationPhase.NotFound).Should().NotEmit(
            TimeSpan.FromMilliseconds(200),
            "during the retry window we must not have declared the page not found");
        service.NavigationContext.Where(ctx => ctx is null).Should().NotEmit(
            TimeSpan.FromMilliseconds(200),
            "the page-not-found flash comes from firing a null context prematurely");
        emissions.Last().Phase.Should().Be(NavigationPhase.LookingUp,
            "while retrying, the status remains 'Looking up'");
    }

    // -- Test #8: retry succeeds â†’ never show NotFound --------------------------

    [Fact]
    public void Status_WhenResolutionFailsOnFirstAttempt_ThenSucceeds_NeverEmitsNotFound()
    {
        _navigationManager.SetUri("http://localhost/eventually/exists");
        // Production uses subscribe-and-stay on the live ResolvePath stream:
        // a single subscription receives null (catalog hasn't learned about the
        // path yet) and waits for a subsequent emission when the catalog changes.
        // Model that with a ReplaySubject — emit null, then later (within the
        // retry budget) emit the successful resolution.
        var resolutionSubject = new System.Reactive.Subjects.ReplaySubject<AddressResolution?>(1);
        resolutionSubject.OnNext(null);
        _pathResolver.ResolvePath("eventually/exists").Returns(resolutionSubject);

        var service = CreateService(retryDelays: [200, 200, 200]);
        var emissions = CaptureStatus(service);

        // InitializeAsync subscribes to the ReplaySubject synchronously; the
        // cached null replays and the pipeline stays at LookingUp (no Redirecting
        // yet). The `emissions` accumulator has LookingUp by the time it returns.
        service.InitializeAsync();
        // Catalog "learned" about the path — re-emit synchronously into the live
        // subscription, driving Redirecting → Loading before the assertion.
        resolutionSubject.OnNext(new AddressResolution("eventually/exists", null));

        emissions.Should().NotContain(s => s.Phase == NavigationPhase.NotFound);
        emissions.Should().Contain(s => s.Phase == NavigationPhase.Redirecting);
    }

    // -- Test #9: OnNavigationContextChanged is not invoked with null mid-retry --

    [Fact]
    public void OnNavigationContextChanged_IsNotInvokedWithNull_BeforeRetriesExhausted()
    {
        _navigationManager.SetUri("http://localhost/does/not/exist");
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(null));

        var service = CreateService(retryDelays: [500, 500, 500]);

        service.InitializeAsync();

        // Firing OnNavigationContextChanged(null) prematurely is the root cause of
        // the 404 flash. Within the retry window (< first 500 ms retry) no null
        // context may surface — a genuine "nothing happens" negative.
        service.NavigationContext.Where(ctx => ctx is null).Should().NotEmit(
            TimeSpan.FromMilliseconds(200),
            "firing OnNavigationContextChanged(null) prematurely is the root cause of the 404 flash");
    }

    // -- Test #10: eventually NotFound is emitted after retries exhaust ----------

    [Fact]
    public void Status_WhenAllRetriesExhaust_EmitsNotFoundAndFiresNullContext()
    {
        _navigationManager.SetUri("http://localhost/does/not/exist");
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(null));

        var nullContextCount = 0;
        var service = CreateService(retryDelays: [5, 5, 5]);
        // Subscribe the counter FIRST, before the assertion below wires its own
        // subscription — Rx OnNext fans out to subscribers in registration order,
        // so the counter is guaranteed to increment before the assertion's
        // FirstAsync unblocks the test thread.
        service.NavigationContext.Subscribe(ctx => { if (ctx is null) nullContextCount++; });
        var emissions = CaptureStatus(service);

        service.InitializeAsync();
        // Stream-wait for the null NavigationContext — the watchdog emits NotFound
        // then a null context as the terminal retry-exhausted action. Blocking on
        // the first null emission guarantees both Status and NavigationContext
        // have fired by the time the assertions run.
        service.NavigationContext.Where(ctx => ctx is null).Should().Within(WaitTimeout).Emit();

        emissions.Should().Contain(s => s.Phase == NavigationPhase.NotFound
                                        && s.Message.Contains("does/not/exist"));
        nullContextCount.Should().Be(1,
            "once retries exhaust, the context event should fire null exactly once");
    }

    // -- Test #11: Loading phase message mentions the address --------------------

    [Fact]
    public void Status_Loading_IncludesAddress()
    {
        _navigationManager.SetUri("http://localhost/ACME/Project/Dashboard");
        _pathResolver.ResolvePath("ACME/Project/Dashboard")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", "Dashboard")));

        var service = CreateService();
        var emissions = CaptureStatus(service);

        service.InitializeAsync();
        // Stream-wait for the Loading emission — replaces Task.Delay(50). Loading
        // is the terminal synchronous phase off the bootstrap (the node load is an
        // empty mesh query), so it is the current BehaviorSubject value.
        service.Status.Should().Within(WaitTimeout)
            .Match(s => s.Phase == NavigationPhase.Loading && s.Message.Contains("ACME/Project"));

        emissions.Should().Contain(s => s.Phase == NavigationPhase.Loading
                                        && s.Message.Contains("ACME/Project"),
            "Loading phase must name the address so the user sees what is being loaded");
    }

    // -- Helpers ------------------------------------------------------------------

    private class MockNavigationManager : NavigationManager
    {
        public MockNavigationManager()
        {
            Initialize("http://localhost/", "http://localhost/");
        }

        public void SetUri(string uri) => Uri = uri;

        public void SimulateLocationChanged(string uri)
        {
            Uri = uri;
            NotifyLocationChanged(isInterceptedLink: false);
        }

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            Uri = new Uri(new Uri(BaseUri), uri).ToString();
            NotifyLocationChanged(isInterceptedLink: false);
        }
    }
}
