using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
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
        _meshQuery.ObserveQuery<MeshNode>(Arg.Any<MeshQueryRequest>(), Arg.Any<JsonSerializerOptions>())
            .Returns(System.Reactive.Linq.Observable.Empty<QueryResultChange<MeshNode>>());
    }

    // Short retries so retry-exhaustion tests run in under ~100 ms total.
    private static readonly int[] FastRetryDelays = [10, 10, 10];

    private NavigationService CreateService(int[]? retryDelays = null) =>
        new(_navigationManager, _pathResolver, _hub, retryDelays ?? FastRetryDelays);

    private static List<NavigationStatus> CaptureStatus(NavigationService service)
    {
        var list = new List<NavigationStatus>();
        service.Status.Subscribe(list.Add);
        return list;
    }

    /// <summary>
    /// Stream-wait for the Status stream to emit a NavigationStatus matching
    /// <paramref name="predicate"/>. Replaces fixed Task.Delay propagation
    /// barriers — those race CI under load. A 15 s timeout surfaces a real
    /// failure with a real stack trace instead of a stale-assertion symptom.
    ///
    /// NOTE: Status is a BehaviorSubject — subscribers see the current value on
    /// subscribe and forward emissions. Most callers want to wait for "did the
    /// pipeline ever emit a matching status?", not "is the current status a
    /// match?". For that case use <see cref="WaitForStatusInList"/> with a
    /// pre-existing accumulator that captured past emissions.
    /// </summary>
    private static Task<NavigationStatus> WaitForStatus(
        NavigationService service,
        Func<NavigationStatus, bool> predicate,
        CancellationToken ct) =>
        service.Status
            .Where(predicate)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(15))
            .ToTask(ct);

    /// <summary>
    /// Wait until the captured <paramref name="emissions"/> list contains any
    /// entry matching <paramref name="predicate"/>. Replaces fixed
    /// Task.Delay(50..200) waits where the test subscribes BEFORE the pipeline
    /// runs and needs the pipeline to have produced the matching emission by
    /// the time the assertions run. Polls on a 50 ms interval via
    /// Observable.Interval — the polling cadence is the only thing fixed; the
    /// .Where predicate is the exit condition; the 15 s Timeout is the
    /// deadline. Compatible with the existing CaptureStatus(service) pattern.
    /// </summary>
    private static Task WaitForStatusInList(
        List<NavigationStatus> emissions,
        Func<NavigationStatus, bool> predicate,
        CancellationToken ct) =>
        Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => emissions.Any(predicate))
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(15))
            .ToTask(ct);

    // -- Test #1: initial subscribers see a non-empty LookingUp message. ----------

    [Fact]
    public async Task Status_AfterInitialize_EmitsNonEmptyLookingUpMessageWithPath()
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

        await service.InitializeAsync();
        // Stream-wait for the LookingUp emission carrying the path — replaces
        // a 50 ms propagation delay. The Status stream is a BehaviorSubject,
        // so the .Where filter is hot on first match.
        var lookingUp = await WaitForStatus(service,
            s => s.Phase == NavigationPhase.LookingUp && s.Message.Contains("FutuRe"),
            TestContext.Current.CancellationToken);

        lookingUp.Should().NotBeNull("after InitializeAsync the LookingUp emission must carry the path");
        lookingUp!.Message.Should().NotBeNullOrWhiteSpace("no spinner without a descriptive label");
        lookingUp.Message.Should().Contain("FutuRe/EuropeRe",
            "the current path should be named in the LookingUp status");
    }

    // -- Test #2: during initial resolution the status says "Looking up <path>". --

    [Fact]
    public async Task Status_DuringInitialResolution_EmitsLookingUpWithPath()
    {
        _navigationManager.SetUri("http://localhost/ACME/Project");
        var resolveStarted = new TaskCompletionSource();
        // AsyncSubject — single emission then complete; fully reactive (no Task bridge).
        var resolveSubject = new System.Reactive.Subjects.AsyncSubject<AddressResolution?>();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(_ =>
            {
                resolveStarted.TrySetResult();
                return resolveSubject;
            });

        var service = CreateService();
        var emissions = CaptureStatus(service);
        var initTask = service.InitializeAsync();

        await resolveStarted.Task;
        // Assert the user would see a "Looking up" message right now.
        emissions.Should().Contain(s => s.Phase == NavigationPhase.LookingUp
                                        && s.Message.Contains("ACME/Project"),
            "user must see what's being looked up, not a blank spinner");

        // Unblock to avoid hanging the test.
        resolveSubject.OnNext(new AddressResolution("ACME/Project", null));
        resolveSubject.OnCompleted();
        await initTask;
    }

    // -- Test #3: after successful resolution, Redirecting with address. ----------

    [Fact]
    public async Task Status_AfterSuccessfulResolution_EmitsRedirectingWithAddress()
    {
        _navigationManager.SetUri("http://localhost/ACME/Project");
        _pathResolver.ResolvePath("ACME/Project")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));

        var service = CreateService();
        var emissions = CaptureStatus(service);

        await service.InitializeAsync();
        // Stream-wait for the Redirecting emission to LAND IN THE LIST —
        // replaces Task.Delay(50). The Status pipeline can run all the way
        // through Looking→Redirecting→Loading→NotFound synchronously off the
        // bootstrap, so a direct service.Status subscription after
        // InitializeAsync may already see "NotFound" and miss intermediate
        // emissions. The `emissions` accumulator captured them all; poll it.
        await WaitForStatusInList(emissions,
            s => s.Phase == NavigationPhase.Redirecting && s.Message.Contains("ACME/Project"),
            TestContext.Current.CancellationToken);

        emissions.Should().Contain(s => s.Phase == NavigationPhase.Redirecting
                                        && s.Message.Contains("ACME/Project"),
            "resolved path should surface 'Redirecting to <address>' to the user");
    }

    // -- Test #4: Redirecting message includes area when non-empty. ---------------

    [Fact]
    public async Task Status_AfterSuccessfulResolution_WithArea_IncludesAreaInMessage()
    {
        _navigationManager.SetUri("http://localhost/ACME/Project/Dashboard");
        _pathResolver.ResolvePath("ACME/Project/Dashboard")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", "Dashboard")));

        var service = CreateService();
        var emissions = CaptureStatus(service);

        await service.InitializeAsync();
        // Stream-wait in the accumulator (Status is a BehaviorSubject — direct
        // service.Status subscribe-after-init may already be on a later phase).
        await WaitForStatusInList(emissions,
            s => s.Phase == NavigationPhase.Redirecting && s.Message.Contains("area Dashboard"),
            TestContext.Current.CancellationToken);

        emissions.Should().Contain(s => s.Phase == NavigationPhase.Redirecting
                                        && s.Message.Contains("area Dashboard"));
    }

    // -- Test #5: Redirecting message omits "area" when area is null. -------------

    [Fact]
    public async Task Status_AfterSuccessfulResolution_WithNoArea_OmitsAreaSuffix()
    {
        _navigationManager.SetUri("http://localhost/ACME/Project");
        _pathResolver.ResolvePath("ACME/Project")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));

        var service = CreateService();
        var emissions = CaptureStatus(service);

        await service.InitializeAsync();
        // Stream-wait in the accumulator (Status is a BehaviorSubject — direct
        // service.Status subscribe-after-init may already be on a later phase).
        await WaitForStatusInList(emissions, s => s.Phase == NavigationPhase.Redirecting,
            TestContext.Current.CancellationToken);

        var redirecting = emissions.FirstOrDefault(s => s.Phase == NavigationPhase.Redirecting);
        redirecting.Should().NotBeNull();
        redirecting!.Message.Should().NotContain("area",
            "area segment must not appear when the resolved path has no area remainder");
    }

    // -- Test #6: THE core "no endless spinner" invariant --------------------------

    [Fact]
    public async Task Status_AllEmissions_HaveNonEmptyMessage()
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

        await service.InitializeAsync();
        _navigationManager.SimulateLocationChanged("http://localhost/does/not/exist");
        // Stream-wait for the NotFound emission — replaces a Task.Delay(200)
        // "wait > total FastRetryDelays" barrier. NotFound is the terminal
        // emission for the failed retry path; once we see it, the lifecycle
        // is over and we can assert on `emissions`.
        await WaitForStatus(service, s => s.Phase == NavigationPhase.NotFound,
            TestContext.Current.CancellationToken);

        emissions.Should().NotBeEmpty();
        emissions.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.Message),
            "no emission â€” including intermediate ones â€” may render as an empty spinner");
    }

    // -- Test #7: retries in flight must NOT emit NotFound / null context --------

    [Fact]
    public async Task Status_WhenResolutionFailsInitially_DoesNotEmitNotFoundUntilRetriesExhausted()
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
        var contextEvents = new List<NavigationContext?>();
        service.NavigationContext.Subscribe(ctx => contextEvents.Add(ctx));

        _ = service.InitializeAsync();
        await Task.Delay(200, TestContext.Current.CancellationToken); // < first retry

        emissions.Should().NotContain(s => s.Phase == NavigationPhase.NotFound,
            "during the retry window we must not have declared the page not found");
        contextEvents.Should().NotContain((NavigationContext?)null,
            "the page-not-found flash comes from firing a null context prematurely");
        emissions.Last().Phase.Should().Be(NavigationPhase.LookingUp,
            "while retrying, the status remains 'Looking up'");
    }

    // -- Test #8: retry succeeds â†’ never show NotFound --------------------------

    [Fact]
    public async Task Status_WhenResolutionFailsOnFirstAttempt_ThenSucceeds_NeverEmitsNotFound()
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

        await service.InitializeAsync();
        // Wait for the LookingUp emission to land in the accumulator before the
        // catalog re-emits — replaces a Task.Delay(50). Without this barrier the
        // resolutionSubject.OnNext below could race the subscription that
        // ProcessLocationChange wires up in response to the bootstrap path.
        await WaitForStatusInList(emissions, s => s.Phase == NavigationPhase.LookingUp,
            TestContext.Current.CancellationToken);
        // Catalog "learned" about the path — re-emit before the watchdog budget expires.
        resolutionSubject.OnNext(new AddressResolution("eventually/exists", null));
        // Stream-wait for Redirecting via the accumulator — Status is a
        // BehaviorSubject and Redirecting → Loading runs synchronously off
        // OnNext, so by the time WaitForStatus subscribes the current is
        // already Loading. WaitForStatusInList scans the full history.
        await WaitForStatusInList(emissions, s => s.Phase == NavigationPhase.Redirecting,
            TestContext.Current.CancellationToken);

        emissions.Should().NotContain(s => s.Phase == NavigationPhase.NotFound);
        emissions.Should().Contain(s => s.Phase == NavigationPhase.Redirecting);
    }

    // -- Test #9: OnNavigationContextChanged is not invoked with null mid-retry --

    [Fact]
    public async Task OnNavigationContextChanged_IsNotInvokedWithNull_BeforeRetriesExhausted()
    {
        _navigationManager.SetUri("http://localhost/does/not/exist");
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(null));

        var nullContextCount = 0;
        var service = CreateService(retryDelays: [500, 500, 500]);
        service.NavigationContext.Subscribe(ctx => { if (ctx is null) nullContextCount++; });

        _ = service.InitializeAsync();
        await Task.Delay(200, TestContext.Current.CancellationToken); // < first retry

        nullContextCount.Should().Be(0,
            "firing OnNavigationContextChanged(null) prematurely is the root cause of the 404 flash");
    }

    // -- Test #10: eventually NotFound is emitted after retries exhaust ----------

    [Fact]
    public async Task Status_WhenAllRetriesExhaust_EmitsNotFoundAndFiresNullContext()
    {
        _navigationManager.SetUri("http://localhost/does/not/exist");
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(null));

        var nullContextCount = 0;
        var service = CreateService(retryDelays: [5, 5, 5]);
        // Subscribe FIRST (counter), then wire the await — Rx OnNext fans out
        // to subscribers in registration order. If the await's subscription
        // came first, its TaskCompletionSource resolves and the test thread
        // could resume before the counter subscription runs. Counter-first
        // guarantees the counter is incremented before the await unblocks.
        service.NavigationContext.Subscribe(ctx => { if (ctx is null) nullContextCount++; });
        var nullContextTask = service.NavigationContext
            .Where(ctx => ctx is null)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(15))
            .ToTask(TestContext.Current.CancellationToken);
        var emissions = CaptureStatus(service);

        await service.InitializeAsync();
        // Stream-wait for the null NavigationContext — replaces a fixed
        // Task.Delay(150) > 3×5ms barrier. The watchdog emits NotFound then
        // null context as the terminal retry-exhausted action; awaiting null
        // context guarantees both Status and NavigationContext have fired.
        await nullContextTask;

        emissions.Should().Contain(s => s.Phase == NavigationPhase.NotFound
                                        && s.Message.Contains("does/not/exist"));
        nullContextCount.Should().Be(1,
            "once retries exhaust, the context event should fire null exactly once");
    }

    // -- Test #11: Loading phase message mentions the address --------------------

    [Fact]
    public async Task Status_Loading_IncludesAddress()
    {
        _navigationManager.SetUri("http://localhost/ACME/Project/Dashboard");
        _pathResolver.ResolvePath("ACME/Project/Dashboard")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", "Dashboard")));

        var service = CreateService();
        var emissions = CaptureStatus(service);

        await service.InitializeAsync();
        // Stream-wait for the Loading emission — replaces Task.Delay(50).
        await WaitForStatus(service,
            s => s.Phase == NavigationPhase.Loading && s.Message.Contains("ACME/Project"),
            TestContext.Current.CancellationToken);

        emissions.Should().Contain(s => s.Phase == NavigationPhase.Loading
                                        && s.Message.Contains("ACME/Project"),
            "Loading phase must name the address so the user sees what is being loaded");
    }

    // -- Helpers ------------------------------------------------------------------

    private static async IAsyncEnumerable<object> ToAsyncObjects(params object[] items)
    {
        foreach (var item in items) yield return item;
        await Task.CompletedTask;
    }

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





