using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using NSubstitute;
using Xunit;
using NavigationContext = MeshWeaver.Mesh.Services.NavigationContext;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// Tests for NavigationService path resolution, event firing, creatable types loading,
/// cancellation, and disposal.
/// </summary>
public class NavigationServiceTest
{
    private readonly MockNavigationManager _navigationManager;
    private readonly IPathResolver _pathResolver;
    private readonly IMeshQueryCore _meshQuery;
    private readonly IMessageHub _hub;
    private readonly IServiceProvider _hubServiceProvider;
    private readonly ICreatableTypesProvider _creatableTypesProvider;

    public NavigationServiceTest()
    {
        _navigationManager = new MockNavigationManager();
        _pathResolver = Substitute.For<IPathResolver>();
        _meshQuery = Substitute.For<IMeshQueryCore>();
        _hub = Substitute.For<IMessageHub>();
        _hubServiceProvider = Substitute.For<IServiceProvider>();
        _creatableTypesProvider = Substitute.For<ICreatableTypesProvider>();

        // ICreatableTypesProvider and IMeshQueryCore are registered at Hub level,
        // not main DI — NavigationService resolves both through
        // hub.ServiceProvider lazily.
        _hub.ServiceProvider.Returns(_hubServiceProvider);
        _hubServiceProvider.GetService(typeof(ICreatableTypesProvider))
            .Returns(_creatableTypesProvider);
        _hubServiceProvider.GetService(typeof(IMeshQueryCore)).Returns(_meshQuery);

        // NavigationService's anonymous gate (ProcessResolvedPath) flips ANY logged-OUT
        // visitor to AccessDenied (→ RedirectToLogin), so the default fixture must run as
        // an AUTHENTICATED visitor for the resolution tests to reach a NavigationContext.
        // We use the System identity: it is non-anonymous (clears the gate) AND
        // TrackNavigationActivity short-circuits on WellKnownUsers.System before the
        // _hub.Post — whose IMessageDelivery<T> return type Castle DynamicProxy cannot
        // proxy (TypeLoadException in CI). The two anonymous tests override this with a
        // virtual/empty context to exercise the gate.
        var systemAccess = new AccessService();
        systemAccess.SetCircuitContext(new AccessContext { ObjectId = WellKnownUsers.System, Name = "System" });
        _hubServiceProvider.GetService(typeof(AccessService)).Returns(systemAccess);

        _hub.Configuration.Returns(new MessageHubConfiguration(null, new Address("test", "nav")));

        // Default stub for IMeshQueryCore.Query — NavigationService now
        // requires a non-null MeshNode to settle Context (commit 8a6f76b10:
        // "null node emits NotFound immediately, never waits for timeout").
        // Without this stub, every test's LoadNodeWithPreRenderedHtml gets an
        // empty observable from the unconfigured mock, the 15s timeout
        // eventually returns null, ProcessResolvedPath takes the null branch
        // and sets Context=null. All 15 NavigationServiceTest cases hit the
        // same path. Default to a minimal placeholder MeshNode that satisfies
        // the LoadNodeWithPreRenderedHtml contract; individual tests override
        // for their specific scenario.
        _meshQuery
            .Query<MeshNode>(Arg.Any<MeshQueryRequest>(), Arg.Any<JsonSerializerOptions>())
            .Returns(call =>
            {
                var req = call.Arg<MeshQueryRequest>();
                // Extract the path:X portion of the query — the path is also
                // the resolved Prefix the test set up via _pathResolver.
                var query = req.Query ?? "";
                var pathStart = query.IndexOf("path:", StringComparison.Ordinal);
                var path = pathStart >= 0
                    ? query[(pathStart + 5)..].Split(' ')[0]
                    : "unknown";
                var node = MeshNode.FromPath(path) with { Name = path, NodeType = "Markdown" };
                return System.Reactive.Linq.Observable.Return(
                    new QueryResultChange<MeshNode>
                    {
                        ChangeType = QueryChangeType.Initial,
                        Items = [node]
                    });
            });
    }

    /// <summary>
    /// Helper to stub <see cref="ICreatableTypesProvider.GetCreatableTypes"/>
    /// with the given items for any <c>parentNode</c>. The provider returns
    /// the entire snapshot as one emission.
    /// </summary>
    private void StubCreatableTypes(string nodePath, params CreatableTypeInfo[] items)
    {
        _creatableTypesProvider
            .GetCreatableTypes(nodePath, Arg.Any<MeshNode?>())
            .Returns(System.Reactive.Linq.Observable.Return((IReadOnlyList<CreatableTypeInfo>)items));
    }

    private NavigationService CreateService() =>
        new(_navigationManager, _pathResolver, _hub);

    /// <summary>
    /// Capture the latest <see cref="NavigationContext"/> emitted by the reactive
    /// stream. NavigationService.NavigationContext is a ReplaySubject(1) — emits
    /// the current value on subscribe and on every change. Tests subscribe to
    /// capture into a local field for assertions.
    /// </summary>
    private sealed class ContextCapture : IDisposable
    {
        private readonly IDisposable _sub;
        public NavigationContext? Latest { get; private set; }
        public int EmissionCount { get; private set; }
        public ContextCapture(NavigationService service)
        {
            _sub = service.NavigationContext.Subscribe(ctx =>
            {
                Latest = ctx;
                EmissionCount++;
            });
        }
        public void Dispose() => _sub.Dispose();
    }

    // Stream-wait timeout for the reactive assertions below. The previous
    // Task-bridging helpers (WaitForContext / WaitForCreatableTypes) used a 15 s
    // budget so a real navigation hang surfaced with a real exception instead of
    // a stale Context assertion; the .Within(...) chains preserve that budget.
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    #region Anonymous Gate Tests

    /// <summary>
    /// Replaces the default System (authenticated) fixture identity with a logged-OUT
    /// visitor: a virtual context resolves to <see cref="WellKnownUsers.Anonymous"/> in
    /// NavigationService.ResolveCurrentUserId, so the anonymous gate engages.
    /// </summary>
    private void MakeVisitorAnonymous()
    {
        var anon = new AccessService();
        anon.SetCircuitContext(new AccessContext
        {
            ObjectId = WellKnownUsers.Anonymous,
            Name = "Guest",
            IsVirtual = true
        });
        _hubServiceProvider.GetService(typeof(AccessService)).Returns(anon);
    }

    /// <summary>
    /// Waits until the mock NavigationManager has been redirected to a URL containing
    /// <paramref name="fragment"/> (the anonymous gate's forceLoad NavigateTo is
    /// synchronous, but poll briefly so the assertion is robust to any scheduling).
    /// </summary>
    private async Task WaitForRedirect(string fragment)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!_navigationManager.Uri.Contains(fragment, StringComparison.Ordinal)
               && sw.Elapsed < WaitTimeout)
            await Task.Delay(10);
    }

    [Fact]
    public async Task AnonymousVisitor_PrivateNode_RedirectsToLogin()
    {
        // A logged-OUT visitor navigates to a node. The anonymous gate redirects them
        // straight to /login?returnUrl=<here> (forceLoad) so login bounces them back
        // afterward.
        MakeVisitorAnonymous();

        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(
                new AddressResolution("Private/Space", "Overview")));

        service.Initialize();
        _navigationManager.SimulateLocationChanged("http://localhost/Private/Space/Overview");

        await WaitForRedirect("/login?returnUrl=");
        _navigationManager.Uri.Should().Contain("/login?returnUrl=");
        // returnUrl carries the page the visitor tried to open, so login can bounce back.
        _navigationManager.Uri.Should().Contain(Uri.EscapeDataString("Private/Space/Overview"));
    }

    [Fact]
    public async Task AnonymousVisitor_PublicNode_RedirectsToLogin()
    {
        // The fix: even a PublicRead page must NOT be shown to a logged-out visitor.
        // PathResolutionService resolves under a System bypass, so the public node still
        // reaches the gate — which now redirects to /login regardless of the node's
        // public-read grant, instead of rendering its content to an anonymous browser.
        // (Previously this resolved Ready, the reported bug.)
        MakeVisitorAnonymous();

        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(
                new AddressResolution("Public/Welcome", "Overview")));

        service.Initialize();
        _navigationManager.SimulateLocationChanged("http://localhost/Public/Welcome/Overview");

        await WaitForRedirect("/login?returnUrl=");
        _navigationManager.Uri.Should().Contain("/login?returnUrl=");
    }

    #endregion

    #region InitializeAsync Tests

    [Fact]
    public async Task InitializeAsync_SubscribesToLocationChanged()
    {
        // Arrange
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));

        // Act
        service.Initialize();

        _pathResolver.ResolvePath("ACME/Project/Overview")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", "Overview")));

        _navigationManager.SimulateLocationChanged("http://localhost/ACME/Project/Overview");

        // Stream-wait for the NavigationContext emission that carries the
        // expected Area — replaces a Task.Delay(100) propagation wait.
        var receivedContext = await service.NavigationContext.Should().Within(WaitTimeout)
            .Match(ctx => ctx?.Area == "Overview");

        receivedContext.Should().NotBeNull();
        receivedContext!.Area.Should().Be("Overview");
    }

    [Fact]
    public async Task InitializeAsync_CalledTwice_OnlySubscribesOnce()
    {
        // Arrange
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));

        // Set the URI so InitializeAsync's bootstrap (PublishPath) actually
        // pushes a non-empty path through the subject — empty paths short-
        // circuit in the production code, leaving the buffer empty and
        // making Skip(1) below count zero on the only emission.
        _navigationManager.SetUri("http://localhost/ACME");

        // Act
        service.Initialize();
        service.Initialize(); // Second call should be idempotent
        // Stream-wait for the bootstrap navigation to materialise into a
        // NavigationContext for "ACME" — replaces a Task.Delay(100) barrier.
        await service.NavigationContext.Should().Within(WaitTimeout)
            .Match(ctx => ctx?.Namespace == "ACME");

        // Assert - verify only one subscription by checking only one event fires
        // per location change. .Skip(1) discards the cached value emitted by
        // ReplaySubject(1) on subscribe (now populated by the bootstrap above).
        var eventCount = 0;
        service.NavigationContext.Skip(1).Subscribe(_ => eventCount++);

        // Use a different path so DistinctUntilChanged doesn't collapse the
        // emission with the bootstrap one. _pathResolver still returns
        // "ACME" namespace for any path (Arg.Any stub above), so the new
        // context carries Path="ACME/Other" with Namespace="ACME".
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/Other");

        // Stream-wait for the navigation to land — when we see Path="ACME/Other"
        // come through the NavigationContext, the LocationChanged handler has
        // fully reacted.
        await service.NavigationContext.Should().Within(WaitTimeout)
            .Match(ctx => ctx?.Path == "ACME/Other");

        eventCount.Should().Be(1);
    }

    [Fact]
    public void InitializeAsync_ProcessesCurrentLocation()
    {
        // Arrange
        var service = CreateService();
        _navigationManager.SetUri("http://localhost/ACME/Project");
        _pathResolver.ResolvePath("ACME/Project")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));

        // Act
        service.Initialize();

        // Assert
        service.Context.Should().NotBeNull();
        service.Context!.Namespace.Should().Be("ACME/Project");
    }

    #endregion

    #region Path Resolution Tests

    [Fact]
    public async Task OnLocationChanged_ResolvesPath_CreatesNavigationContext()
    {
        // Arrange
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));
        service.Initialize();

        _pathResolver.ResolvePath("ACME/Project/Dashboard/123")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", "Dashboard/123")));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/Project/Dashboard/123");
        await service.NavigationContext.Should().Within(WaitTimeout)
            .Match(ctx => ctx?.Path == "ACME/Project/Dashboard/123");

        // Assert
        service.Context.Should().NotBeNull();
        service.Context!.Path.Should().Be("ACME/Project/Dashboard/123");
        service.Context.Namespace.Should().Be("ACME/Project");
    }

    [Fact]
    public async Task OnLocationChanged_RaisesNavigationContextChangedEvent()
    {
        // Arrange
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));
        service.Initialize();

        _pathResolver.ResolvePath("ACME/Project")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/Project");
        var receivedContext = await service.NavigationContext.Should().Within(WaitTimeout)
            .Match(ctx => ctx?.Namespace == "ACME/Project");

        // Assert
        receivedContext.Should().NotBeNull();
        receivedContext!.Namespace.Should().Be("ACME/Project");
    }

    [Fact]
    public async Task OnLocationChanged_WhenResolutionNull_KeepsStaleContext_UntilRetriesExhaust()
    {
        // The old behavior cleared Context and fired OnNavigationContextChanged(null)
        // immediately, which caused the "Page Not Found" card to flash before the
        // retry loop had a chance to succeed. The fix: keep the previous context
        // stale while we retry, and only fire the null/NotFound transition once
        // retries are exhausted.
        //
        // The not-found watchdog only fires after the whole retry budget (~1 s in
        // production) is exhausted, so 100 ms is comfortably inside the window where
        // the previous context must still be shown — long enough to assert the null
        // callback is NOT fired early.
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));
        // Set URI so InitializeAsync's bootstrap actually triggers ProcessLocationChange
        // — empty paths short-circuit in production via PublishPath.
        _navigationManager.SetUri("http://localhost/ACME");
        service.Initialize();
        // Stream-wait for the bootstrap navigation to land — replaces a
        // Task.Delay(50) propagation barrier.
        await service.NavigationContext.Should().Within(WaitTimeout)
            .Match(ctx => ctx?.Namespace == "ACME");

        var previousContext = service.Context;
        previousContext.Should().NotBeNull();

        _pathResolver.ResolvePath("unknown/path")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(null));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/unknown/path");

        // Assert: during the retry window the UI still reports the previous resolved
        // context (or at least did not receive a null-context invocation that would
        // trigger the "Page Not Found" render). 100 ms < first retry delay (500 ms).
        await service.NavigationContext.Where(ctx => ctx is null).Should().NotEmit(
            TimeSpan.FromMilliseconds(100),
            "the 404 flash bug was caused by firing a null context before retries had a chance");
    }

    [Fact]
    public async Task OnLocationChanged_WhenPathNeverResolves_ReportsNotFoundAfterBudget()
    {
        // A genuinely missing page: the catalog never learns the path, so ResolvePath
        // only ever emits null and no resolution arrives. Once the (short, ~1 s in
        // production) retry budget is exhausted, the watchdog MUST flip Status to
        // NotFound — the regression was that a missing page kept the stale page up and
        // never surfaced the "not found" card. Tiny delays via the internal ctor drive
        // the exhaustion path in a few ms.
        var service = new NavigationService(
            _navigationManager, _pathResolver, _hub, new[] { 5, 5, 5 });
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(null));
        _navigationManager.SetUri("http://localhost/does/not/exist");

        // Act — bootstrap navigation resolves to null, watchdog fires after ~15 ms.
        service.Initialize();

        // Assert — Status reaches NotFound (not stuck Loading / stale forever).
        var status = await service.Status.Should().Within(WaitTimeout)
            .Match(s => s.Phase == NavigationPhase.NotFound);
        status.Phase.Should().Be(NavigationPhase.NotFound);
    }

    [Fact]
    public async Task OnLocationChanged_WhenFirstSnapshotEmptyThenResolves_RetriesAndResolves()
    {
        // 🚨 Regression (dead page / "view at ``"): ResolvePath is a ONE-SHOT
        // Take(1) snapshot. During partition warm-up / NodeType compile the FIRST
        // snapshot can be empty (null) for a path that DOES exist. The old code
        // subscribed once and settled that transient negative as a PERMANENT
        // NotFound (Context=null) — the page stayed dead until a manual reload. The
        // fix re-ASKS the resolver across the retry budget, so a path that becomes
        // resolvable on a LATER probe still lands. The negative is never cached.
        var service = new NavigationService(
            _navigationManager, _pathResolver, _hub, new[] { 5, 5, 5 });

        // First probe: empty (catalog hasn't learned the path yet). Subsequent
        // probes: the node is now present → resolves.
        _pathResolver.ResolvePath("rbuergi/_Thread/hello-73ac")
            .Returns(
                System.Reactive.Linq.Observable.Return<AddressResolution?>(null),
                System.Reactive.Linq.Observable.Return<AddressResolution?>(
                    new AddressResolution("rbuergi/_Thread/hello-73ac", null)));

        service.Initialize();
        _navigationManager.SimulateLocationChanged("http://localhost/rbuergi/_Thread/hello-73ac");

        // The transient first-empty must NOT settle as NotFound — a later probe resolves.
        var ctx = await service.NavigationContext.Should().Within(WaitTimeout)
            .Match(c => c?.Namespace == "rbuergi/_Thread/hello-73ac");
        ctx.Should().NotBeNull();
        service.Context!.Namespace.Should().Be("rbuergi/_Thread/hello-73ac");

        // It re-asked the resolver rather than settling on the single one-shot null.
        _ = _pathResolver.Received(2).ResolvePath("rbuergi/_Thread/hello-73ac");
    }

    [Fact]
    public async Task OnLocationChanged_ParsesRemainderIntoAreaAndId()
    {
        // Arrange
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));
        service.Initialize();

        _pathResolver.ResolvePath("ACME/Project/Dashboard/item-123")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", "Dashboard/item-123")));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/Project/Dashboard/item-123");
        await service.NavigationContext.Should().Within(WaitTimeout)
            .Match(ctx => ctx?.Area == "Dashboard" && ctx?.Id == "item-123");

        // Assert
        service.Context.Should().NotBeNull();
        service.Context!.Area.Should().Be("Dashboard");
        service.Context.Id.Should().Be("item-123");
    }

    [Fact]
    public async Task OnLocationChanged_WithNoRemainder_SetsAreaAndIdNull()
    {
        // Arrange
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));
        service.Initialize();

        _pathResolver.ResolvePath("ACME/Project")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/Project");
        await service.NavigationContext.Should().Within(WaitTimeout)
            .Match(ctx => ctx?.Namespace == "ACME/Project" && ctx.Area == null);

        // Assert
        service.Context.Should().NotBeNull();
        service.Context!.Area.Should().BeNull();
        service.Context.Id.Should().BeNull();
    }

    [Fact]
    public async Task OnLocationChanged_WithAreaOnlyRemainder_SetsIdNull()
    {
        // Arrange
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));
        service.Initialize();

        _pathResolver.ResolvePath("ACME/Project/Dashboard")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", "Dashboard")));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/Project/Dashboard");
        await service.NavigationContext.Should().Within(WaitTimeout)
            .Match(ctx => ctx?.Area == "Dashboard" && ctx.Id == null);

        // Assert
        service.Context.Should().NotBeNull();
        service.Context!.Area.Should().Be("Dashboard");
        service.Context.Id.Should().BeNull();
    }

    [Fact]
    public async Task OnLocationChanged_ConfigNodeWithAreaSuffix_ResolvesAreaCorrectly()
    {
        // Arrange - simulates Organization/Search where Organization is a config node
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));
        service.Initialize();

        // Organization/Search: address is "Organization", remainder is "Search"
        _pathResolver.ResolvePath("Organization/Search")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("Organization", "Search")));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/Organization/Search");
        await service.NavigationContext.Should().Within(WaitTimeout)
            .Match(ctx => ctx?.Namespace == "Organization" && ctx.Area == "Search");

        // Assert
        service.Context.Should().NotBeNull();
        service.Context!.Area.Should().Be("Search");
        service.Context.Id.Should().BeNull();
        service.Context.Namespace.Should().Be("Organization");
    }

    [Fact]
    public async Task OnLocationChanged_ConfigNodeWithAreaAndId_ResolvesCorrectly()
    {
        // Arrange - simulates Organization/Settings/Metadata
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));
        service.Initialize();

        _pathResolver.ResolvePath("Organization/Settings/Metadata")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("Organization", "Settings/Metadata")));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/Organization/Settings/Metadata");
        await service.NavigationContext.Should().Within(WaitTimeout)
            .Match(ctx => ctx?.Namespace == "Organization" && ctx.Area == "Settings" && ctx.Id == "Metadata");

        // Assert
        service.Context.Should().NotBeNull();
        service.Context!.Area.Should().Be("Settings");
        service.Context.Id.Should().Be("Metadata");
        service.Context.Namespace.Should().Be("Organization");
    }

    [Fact]
    public async Task OnLocationChanged_UserNodeWithArea_ResolvesCorrectly()
    {
        // Arrange - simulates User/Roland/Settings where User/Roland is a persisted node
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));
        service.Initialize();

        _pathResolver.ResolvePath("User/Roland/Settings")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("User/Roland", "Settings")));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/User/Roland/Settings");
        await service.NavigationContext.Should().Within(WaitTimeout)
            .Match(ctx => ctx?.Namespace == "User/Roland" && ctx.Area == "Settings");

        // Assert
        service.Context.Should().NotBeNull();
        service.Context!.Area.Should().Be("Settings");
        service.Context.Id.Should().BeNull();
        service.Context.Namespace.Should().Be("User/Roland");
    }

    #endregion

    #region Query String / Args Splitting Tests

    [Fact]
    public void SingleSegmentRouteWithQuery_IsPageRoute_NotResolvedAsNode()
    {
        // 🚨 Regression: the AI top-bar "Threads" / "Models" items navigate to
        //   /search?q=nodeType%3AThread&groupBy=Namespace
        // The whole URL (query included) used to be fed into path resolution, so the
        // resolved address became the synthetic node `search?q=nodeType:Thread&...`, the
        // `nodeType:Thread` token got permission-checked as a Thread node ("lacks Thread
        // permission"), AND a GetDataRequest to that bogus hub hung >30s without opening its
        // init gates. A single-segment route that carries query params is a Blazor PAGE route,
        // never a mesh node: it must NOT be resolved/permission-checked/subscribed at all.
        var service = CreateService();
        // Stub the resolver to "succeed" for anything — the assertion is that it is NEVER called.
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(
                new AddressResolution("search", null)));

        service.Initialize();
        // ProcessLocationChange runs synchronously off the path subject, so the page-route
        // short-circuit has already emitted by the time this returns.
        _navigationManager.SimulateLocationChanged("http://localhost/search?q=nodeType%3AThread&groupBy=Namespace");

        // No node address: Context cleared, namespace cleared.
        service.Context.Should().BeNull();
        service.CurrentNamespace.Should().BeNull();
        service.IsResolving.Should().BeFalse();

        // The resolver was NEVER asked to resolve a page route — not the bare route and
        // certainly not the query-laden URL.
        _ = _pathResolver.DidNotReceive().ResolvePath(Arg.Any<string>());
    }

    [Fact]
    public async Task RealNodeUrl_NoQuery_ResolvesAddressCorrectly_ArgsEmpty()
    {
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(null));
        _pathResolver.ResolvePath("AgenticPension/Jahresrechnung")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(
                new AddressResolution("AgenticPension/Jahresrechnung", null)));

        service.Initialize();
        _navigationManager.SimulateLocationChanged("http://localhost/AgenticPension/Jahresrechnung");

        var ctx = await service.NavigationContext.Should().Within(WaitTimeout)
            .Match(c => c?.Path == "AgenticPension/Jahresrechnung");

        ctx!.Address.ToString().Should().Be("AgenticPension/Jahresrechnung");
        ctx.Namespace.Should().Be("AgenticPension/Jahresrechnung");
        ctx.Args.Should().BeEmpty();
        _ = _pathResolver.Received().ResolvePath("AgenticPension/Jahresrechnung");
    }

    [Fact]
    public async Task NodeAreaUrl_WithQuery_ResolvesNodeAddress_QueryBecomesArgs()
    {
        // A node/area URL that carries a legitimate query parameter: the route (node+area)
        // resolves to the node address; the query rides on Args, never on the address.
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(null));
        _pathResolver.ResolvePath("AgenticPension/Overview")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(
                new AddressResolution("AgenticPension", "Overview")));

        service.Initialize();
        _navigationManager.SimulateLocationChanged("http://localhost/AgenticPension/Overview?tab=summary&edit=true");

        var ctx = await service.NavigationContext.Should().Within(WaitTimeout)
            .Match(c => c?.Area == "Overview");

        ctx!.Address.ToString().Should().Be("AgenticPension");
        ctx.Path.Should().Be("AgenticPension/Overview");
        ctx.Args["tab"].Should().Be("summary");
        ctx.Args["edit"].Should().Be("true");
        _ = _pathResolver.Received().ResolvePath("AgenticPension/Overview");
        _ = _pathResolver.DidNotReceive().ResolvePath(Arg.Is<string>(p => p.Contains('?')));
    }

    #endregion

    #region Creatable Types Tests

    [Fact]
    public async Task OnLocationChanged_WhenMeshNodeChanges_ReloadsCreatableTypes()
    {
        // Arrange
        var service = CreateService();
        CreatableTypesSnapshot? lastSnapshot = null;
        service.CreatableTypes.Subscribe(s => lastSnapshot = s);

        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));

        StubCreatableTypes("ACME", new CreatableTypeInfo("ACME/Todo"));

        // Set URI so InitializeAsync's bootstrap actually triggers a
        // ProcessLocationChange (PublishPath skips empty paths). Then
        // stream-wait for the ACME creatable-types load to settle so the
        // subsequent navigation kicks off a CLEAN second load.
        _navigationManager.SetUri("http://localhost/ACME");
        service.Initialize();
        await service.CreatableTypes.Should().Within(WaitTimeout)
            .Match(s => !s.IsLoading && s.Items.Any(t => t.NodeTypePath == "ACME/Todo"));

        // Change to different node path
        _pathResolver.ResolvePath("ACME/Project")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));

        StubCreatableTypes("ACME/Project",
            new CreatableTypeInfo("ACME/Project/Story"),
            new CreatableTypeInfo("ACME/Project/Todo"));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/Project");
        // Stream-wait for the second snapshot (2 items, Done) — replaces a
        // Task.Delay(200) propagation barrier.
        await service.CreatableTypes.Should().Within(WaitTimeout)
            .Match(s => !s.IsLoading && s.Items.Count == 2
                && s.Items.Any(t => t.NodeTypePath == "ACME/Project/Story")
                && s.Items.Any(t => t.NodeTypePath == "ACME/Project/Todo"));

        // Assert
        lastSnapshot.Should().NotBeNull();
        lastSnapshot!.Items.Should().HaveCount(2);
        lastSnapshot.Items.Should().Contain(t => t.NodeTypePath == "ACME/Project/Story");
        lastSnapshot.Items.Should().Contain(t => t.NodeTypePath == "ACME/Project/Todo");
    }

    [Fact]
    public async Task OnLocationChanged_WhenMeshNodeSame_DoesNotReloadCreatableTypes()
    {
        // Arrange
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));

        var loadCount = 0;
        _creatableTypesProvider
            .GetCreatableTypes("ACME/Project", Arg.Any<MeshNode?>())
            .Returns(_ =>
            {
                loadCount++;
                return System.Reactive.Linq.Observable.Return(
                    (IReadOnlyList<CreatableTypeInfo>)new[] { new CreatableTypeInfo("ACME/Project/Todo") });
            });

        // Set URI so InitializeAsync's bootstrap actually triggers the first
        // creatable-types load (otherwise PublishPath("") short-circuits and
        // the first load doesn't happen until SimulateLocationChanged below).
        _navigationManager.SetUri("http://localhost/ACME/Project");
        service.Initialize();
        // Stream-wait for the first load to complete — replaces Task.Delay(100).
        await service.CreatableTypes.Should().Within(WaitTimeout)
            .Match(s => !s.IsLoading && s.Items.Any(t => t.NodeTypePath == "ACME/Project/Todo"));

        // Navigate to different area within same node
        _pathResolver.ResolvePath("ACME/Project/Dashboard")
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", "Dashboard")));

        // Act
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/Project/Dashboard");
        // Stream-wait for the navigation to land (Context.Area=="Dashboard")
        // — that's the positive signal that ProcessLocationChange ran to
        // completion. If a creatable-types reload were going to fire, it
        // would have done so by now. Replaces a Task.Delay(100) negative wait
        // with a synchronous "navigation done" wait.
        await service.NavigationContext.Should().Within(WaitTimeout)
            .Match(ctx => ctx?.Area == "Dashboard");

        // Assert - should only load once (during initialization)
        loadCount.Should().Be(1);
    }

    [Fact]
    public async Task LoadCreatableTypes_EmitsLoadingThenDoneSnapshots()
    {
        // Arrange
        var service = CreateService();
        var snapshots = new List<CreatableTypesSnapshot>();

        service.CreatableTypes.Subscribe(s => snapshots.Add(s));

        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME/Project", null)));

        StubCreatableTypes("ACME/Project",
            new CreatableTypeInfo("ACME/Project/Story"),
            new CreatableTypeInfo("ACME/Project/Todo"));

        // Set URI so InitializeAsync triggers ProcessLocationChange
        // (PublishPath skips empty initial paths in production).
        _navigationManager.SetUri("http://localhost/ACME/Project");

        // Act
        service.Initialize();
        // Stream-wait for the Done snapshot — replaces Task.Delay(300). The
        // Loading snapshot lands synchronously before this returns, so the
        // accumulator will have captured both states.
        await service.CreatableTypes.Should().Within(WaitTimeout)
            .Match(s => !s.IsLoading && s.Items.Count == 2);

        // Assert — observable provider emits the whole snapshot:
        // Loading (empty) is published synchronously before the subscription
        // resolves; Done (with items) lands after the first emission.
        snapshots.Should().Contain(s => s.Items.Count == 0 && s.IsLoading);
        snapshots.Should().Contain(s => s.Items.Count == 2 && !s.IsLoading);
    }

    [Fact]
    public async Task CreatableTypesSnapshot_IsLoadingTransitions()
    {
        // Arrange
        var service = CreateService();
        var loadingStates = new List<bool>();

        service.CreatableTypes.Subscribe(s => loadingStates.Add(s.IsLoading));

        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));

        StubCreatableTypes("ACME", new CreatableTypeInfo("ACME/Todo"));

        // Set URI so InitializeAsync triggers ProcessLocationChange
        // (PublishPath skips empty initial paths in production).
        _navigationManager.SetUri("http://localhost/ACME");

        // Act
        service.Initialize();
        // Stream-wait for the IsLoading=false transition with items present —
        // replaces a Task.Delay(200). The IsLoading=true emission lands
        // synchronously before this returns, so loadingStates has both.
        await service.CreatableTypes.Should().Within(WaitTimeout)
            .Match(s => !s.IsLoading && s.Items.Count > 0);

        // Assert - should see true while loading, false when done
        loadingStates.Should().Contain(true);
        loadingStates.Last().Should().BeFalse();
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_UnsubscribesFromLocationChanged()
    {
        // Arrange
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("ACME", null)));
        service.Initialize();

        var eventFired = false;
        // .Skip(1) discards the cached value (ReplaySubject) so we only count NEW emissions.
        // Subscribe BEFORE Dispose — Dispose() tears down the NavigationContext subject,
        // so a post-dispose subscribe would throw ObjectDisposedException.
        service.NavigationContext.Skip(1).Subscribe(_ => eventFired = true);

        // Act
        service.Dispose();
        _navigationManager.SimulateLocationChanged("http://localhost/ACME/New");

        // Assert - Dispose detached the LocationChanged handler, so the simulated
        // change drives zero subscribers and the synchronous resolution path never
        // runs. eventFired stays false with no propagation wait required.
        eventFired.Should().BeFalse();
    }

    #endregion

    #region Satellite Node Tests

    [Fact]
    public async Task OnLocationChanged_SatelliteNode_CurrentNamespacePointsAtMainNode()
    {
        // User browses to a thread under PartnerRe/AIConsulting. The thread node's MainNode
        // points back at the parent that owns it, so CurrentNamespace â€” which downstream
        // chat/autocomplete/attachment code uses to resolve relative paths â€” must surface
        // the main node, not the satellite path.
        var service = CreateService();
        const string SatellitePath = "PartnerRe/AIConsulting/_Thread/abc-123";
        const string MainNode = "PartnerRe/AIConsulting";

        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution(SatellitePath, null)));

        var threadNode = new MeshNode("abc-123", "PartnerRe/AIConsulting/_Thread")
        {
            NodeType = "Thread",
            MainNode = MainNode
        };
        _meshQuery.Query<MeshNode>(Arg.Any<MeshQueryRequest>(), Arg.Any<JsonSerializerOptions>())
            .Returns(System.Reactive.Linq.Observable.Return(QueryChange(threadNode)));

        service.Initialize();
        // The MockNavigationManager initializes at "http://localhost/" (empty
        // relative path) and PublishPath("") returns early, so InitializeAsync's
        // bootstrap is a no-op. Trigger the navigation explicitly via
        // SimulateLocationChanged — same shape as the other passing tests.
        _navigationManager.SimulateLocationChanged($"http://localhost/{SatellitePath}");
        // Stream-wait for the navigation to land with the satellite context.
        await service.NavigationContext.Should().Within(WaitTimeout)
            .Match(ctx => ctx?.IsSatellite == true);

        service.CurrentNamespace.Should().Be(MainNode);
        service.Context!.Namespace.Should().Be(SatellitePath);
        service.Context.PrimaryPath.Should().Be(MainNode);
        service.Context.IsSatellite.Should().BeTrue();
    }

    [Fact]
    public async Task OnLocationChanged_RegularNode_CurrentNamespaceMatchesNamespace()
    {
        // For a non-satellite node, CurrentNamespace and Namespace are the same.
        // PrimaryPath falls back to Namespace when Node is null, so this also covers
        // the no-node-found path (existing tests rely on this fallback).
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("PartnerRe/AIConsulting", null)));

        var mainNode = new MeshNode("AIConsulting", "PartnerRe")
        {
            NodeType = "Group"
            // MainNode defaults to Path → "PartnerRe/AIConsulting"
        };
        _meshQuery.Query<MeshNode>(Arg.Any<MeshQueryRequest>(), Arg.Any<JsonSerializerOptions>())
            .Returns(System.Reactive.Linq.Observable.Return(QueryChange(mainNode)));

        service.Initialize();
        // The MockNavigationManager initializes at "http://localhost/" (empty
        // relative path) and PublishPath("") returns early, so InitializeAsync's
        // bootstrap is a no-op. Trigger the navigation explicitly — same shape
        // as OnLocationChanged_ResolvesPath_CreatesNavigationContext above.
        _navigationManager.SimulateLocationChanged("http://localhost/PartnerRe/AIConsulting");
        // Stream-wait for the navigation to land — PrimaryPath becoming the
        // main node path is the positive signal.
        await service.NavigationContext.Should().Within(WaitTimeout)
            .Match(ctx => ctx?.PrimaryPath == "PartnerRe/AIConsulting");

        service.CurrentNamespace.Should().Be("PartnerRe/AIConsulting");
        service.Context!.PrimaryPath.Should().Be("PartnerRe/AIConsulting");
        service.Context.IsSatellite.Should().BeFalse();
    }

    [Fact]
    public async Task OnLocationChanged_SatelliteNode_LoadsCreatableTypesForMainNode()
    {
        // The creatable-types background load also keys off PrimaryPath so menus on
        // satellite pages reflect what can be created on the parent node.
        var service = CreateService();
        _pathResolver.ResolvePath(Arg.Any<string>())
            .Returns(System.Reactive.Linq.Observable.Return<AddressResolution?>(new AddressResolution("PartnerRe/AIConsulting/_Thread/abc-123", null)));

        var threadNode = new MeshNode("abc-123", "PartnerRe/AIConsulting/_Thread")
        {
            NodeType = "Thread",
            MainNode = "PartnerRe/AIConsulting"
        };
        _meshQuery.Query<MeshNode>(Arg.Any<MeshQueryRequest>(), Arg.Any<JsonSerializerOptions>())
            .Returns(System.Reactive.Linq.Observable.Return(QueryChange(threadNode)));

        StubCreatableTypes("PartnerRe/AIConsulting",
            new CreatableTypeInfo("PartnerRe/AIConsulting/Story"));

        CreatableTypesSnapshot? lastSnapshot = null;
        service.CreatableTypes.Subscribe(s => lastSnapshot = s);

        // Set URI so InitializeAsync triggers ProcessLocationChange
        // (PublishPath skips empty initial paths in production).
        _navigationManager.SetUri("http://localhost/PartnerRe/AIConsulting/_Thread/abc-123");

        service.Initialize();
        // Stream-wait for the creatable-types load for the main node to land
        // — replaces a Task.Delay(150).
        await service.CreatableTypes.Should().Within(WaitTimeout)
            .Match(s => !s.IsLoading && s.Items.Any(t => t.NodeTypePath == "PartnerRe/AIConsulting/Story"));

        // Discard the IObservable result — this is an NSubstitute received-call
        // verification, not a real invocation. The discard silences CS4014
        // (System.Reactive's GetAwaiter makes IObservable<T> awaitable).
        _ = _creatableTypesProvider.Received().GetCreatableTypes(
            "PartnerRe/AIConsulting", Arg.Any<MeshNode?>());
        lastSnapshot!.Items.Should().Contain(t => t.NodeTypePath == "PartnerRe/AIConsulting/Story");
    }

    #endregion

    #region Helper Methods

    private static QueryResultChange<MeshNode> QueryChange(params MeshNode[] items) =>
        new()
        {
            ChangeType = QueryChangeType.Initial,
            Items = items,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow,
        };

    #endregion

    #region Mock Navigation Manager

    /// <summary>
    /// Mock NavigationManager that allows simulating location changes.
    /// </summary>
    private class MockNavigationManager : NavigationManager
    {
        public MockNavigationManager()
        {
            Initialize("http://localhost/", "http://localhost/");
        }

        public void SetUri(string uri)
        {
            Uri = uri;
        }

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

    #endregion
}
