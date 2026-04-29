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
    private readonly INodeTypeService _nodeTypeService;

    public NavigationProgressTest()
    {
        _navigationManager = new MockNavigationManager();
        _pathResolver = Substitute.For<IPathResolver>();
        _meshQuery = Substitute.For<IMeshQueryCore>();
        _hub = Substitute.For<IMessageHub>();
        _hubServiceProvider = Substitute.For<IServiceProvider>();
        _nodeTypeService = Substitute.For<INodeTypeService>();

        _hub.ServiceProvider.Returns(_hubServiceProvider);
        _hubServiceProvider.GetService(typeof(INodeTypeService)).Returns(_nodeTypeService);
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

    // -- Test #1: initial subscribers see a non-empty LookingUp message. ----------

    [Fact]
    public void Status_BeforeInitialize_EmitsNonEmptyLookingUpMessage()
    {
        // The BehaviorSubject must start with a status that tells the user
        // something â€” never a silent initial state that renders as a spinner
        // with no label.
        _navigationManager.SetUri("http://localhost/FutuRe/EuropeRe");
        var service = CreateService();

        NavigationStatus? initial = null;
        service.Status.Subscribe(s => { initial = s; });

        initial.Should().NotBeNull("BehaviorSubject must always have a current value");
        initial!.Message.Should().NotBeNullOrWhiteSpace("no spinner without a descriptive label");
        initial.Phase.Should().Be(NavigationPhase.LookingUp,
            "before resolution completes we should tell the user we're looking up");
        initial.Message.Should().Contain("FutuRe/EuropeRe",
            "the current path should be named in the initial status");
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
        await Task.Delay(50, TestContext.Current.CancellationToken);

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
        await Task.Delay(50, TestContext.Current.CancellationToken);

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
        await Task.Delay(50, TestContext.Current.CancellationToken);

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
        await Task.Delay(200, TestContext.Current.CancellationToken); // > total FastRetryDelays

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
        await Task.Delay(50, TestContext.Current.CancellationToken);
        // Catalog "learned" about the path — re-emit before the watchdog budget expires.
        resolutionSubject.OnNext(new AddressResolution("eventually/exists", null));
        await Task.Delay(100, TestContext.Current.CancellationToken);

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
        service.NavigationContext.Subscribe(ctx => { if (ctx is null) nullContextCount++; });
        var emissions = CaptureStatus(service);

        await service.InitializeAsync();
        await Task.Delay(150, TestContext.Current.CancellationToken); // > 3Ã—5ms + margin

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
        await Task.Delay(50, TestContext.Current.CancellationToken);

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





