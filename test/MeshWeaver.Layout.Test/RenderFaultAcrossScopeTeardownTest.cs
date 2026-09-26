using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// 🚨 <b>A nested-area render fault that lands WHILE the host's DI scope is being disposed must
/// never throw out of the render's error arm.</b>
///
/// <para><b>What CI reported</b> (MeshWeaver.Plugins core-candidate run 36267738287, suite
/// <c>MeshWeaver.AI.Test</c>, exit 134 with every test green):</para>
/// <code>
/// Unhandled exception. System.ObjectDisposedException: Instances cannot be resolved … from this LifetimeScope …
///    at Autofac.Extensions.DependencyInjection.AutofacServiceProvider.GetService(Type serviceType)
///    at MeshWeaver.Layout.Composition.LayoutAreaLocalizationExtensions.Localize(LayoutAreaHost host, …)
///    at MeshWeaver.Layout.Composition.LayoutAreaHost.CreateRenderErrorControl(Exception ex)
///    at MeshWeaver.Layout.Composition.LayoutAreaHost.FailRendering(Exception ex, String area)
///    at MeshWeaver.Layout.Composition.LayoutAreaHost.&lt;RenderArea&gt;b__1(Exception ex)
///    at System.Reactive.Subjects.Subject`1.OnError(Exception error)
///    at MeshWeaver.Messaging.OwnedConnectionExtensions.ReleaseSignal.&lt;Fire&gt;b__6_0()
///    at MeshWeaver.Messaging.ReleaseLane.Run(Action release)
/// </code>
///
/// <para><b>Why #2679's probe did not cover it.</b> <c>FailRendering</c> probes the host's scope ONCE
/// and skips the placeholder when it is gone. But the error arm runs off the hub (here on the
/// release lane) and the scope is disposed on ANOTHER thread, so it can close between the probe and
/// the placeholder: the probe said "alive", the localisation resolved <c>AccessService</c> from the
/// hub's provider a few statements later, Autofac threw, and the throw left the Rx error arm onto a
/// thread-pool thread — which kills the process. A check-then-act on a scope someone else disposes
/// cannot be made safe by checking harder.</para>
///
/// <para><b>The fix</b> removes the ACT's dependency on the scope: <c>AccessService</c> and the
/// render-subscribe scheduler are mesh-lifetime singletons (registered on the root, inherited by
/// every hosted hub), so the host captures them while it is being constructed and never asks a
/// container again on a late path. Hoisting is the complete fix here because the service's lifetime
/// is the MESH's, not the dying hub's (DisposedScopeAndDyingHubs → R3).</para>
///
/// <para><b>How the window is pinned, deterministically.</b> The host logs the render fault
/// (<c>Rendering failed for area …</c>) strictly BETWEEN the probe and the placeholder. The test's
/// logger disposes the host hub's service provider synchronously inside that log call — so the
/// probe has already answered "alive" and the placeholder then runs against a dead scope, on every
/// run. The fault is raised from the test thread by <see cref="Subject{T}.OnError"/>, and Rx
/// forwards an error arm's throw to the caller of <c>OnError</c>, so the unfixed code fails THIS
/// test instead of the process.</para>
/// </summary>
public class RenderFaultAcrossScopeTeardownTest : HubTestBase
{
    private const string OuterView = nameof(OuterView);
    private const string ChildArea = nameof(ChildArea);

    private readonly Subject<UiControl?> child = new();
    private readonly ReplaySubject<Unit> childSubscribed = new(1);
    private readonly FaultLogHook hook = new();

    public RenderFaultAcrossScopeTeardownTest(ITestOutputHelper output) : base(output)
    {
        Services.AddLogging(l =>
        {
            l.Services.AddSingleton<ILoggerProvider>(hook);
            l.AddFilter<FaultLogHook>(typeof(LayoutAreaHost).FullName, LogLevel.Trace);
        });
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                .WithView(OuterView, (LayoutAreaHost _, RenderingContext _) =>
                    Observable.Return<UiControl?>(Controls.Stack.WithView(
                        Observable.Defer(() =>
                        {
                            childSubscribed.OnNext(Unit.Default);
                            return child;
                        }),
                        ChildArea))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient(d => d);

    /// <summary>
    /// RED before the fix: <c>child.OnError</c> throws <see cref="ObjectDisposedException"/> out of
    /// <c>LayoutAreaHost.CreateRenderErrorControl → Localize</c> — the process-killing throw of the CI
    /// run, surfaced here on the test thread. GREEN after: the error arm completes, and the fault is
    /// reported exactly once.
    /// </summary>
    [HubFact]
    public async Task ANestedRenderFault_WhoseScopeClosesMidReport_NeverThrowsOutOfTheErrorArm()
    {
        var host = GetHost();
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(OuterView));
        using var subscription = stream.Subscribe(_ => { }, _ => { });

        // The nested area's generator is subscribed: its error arm is FailRendering(ex, area).
        await childSubscribed.FirstAsync().Timeout(TimeSpan.FromSeconds(10)).Await();

        // The window: dispose the host hub's scope from INSIDE the host's own fault report — after
        // FailRendering's probe answered "alive", before it builds the placeholder.
        hook.OnRenderFaultLogged = () => ((IDisposable)host.ServiceProvider).Dispose();

        var thrown = Record.Exception(() =>
            child.OnError(new InvalidOperationException("the child view generator faulted")));

        Output.WriteLine($"thrown out of the error arm: {thrown}");
        hook.Fired.Should().Be(1,
            "the window was actually entered: the scope was disposed between the probe and the placeholder");
        thrown.Should().BeNull(
            "a render's error arm runs off the hub — on the release lane in the CI crash — so anything "
            + "it throws escapes onto a pool thread and kills the process (exit 134). The placeholder "
            + "must not depend on a DI scope another thread can dispose");

        await host.DisposalCompleted.FirstAsync().Timeout(TimeSpan.FromSeconds(15)).Await();
    }

    /// <summary>
    /// A LayoutAreaHost-category logger that runs the test's action synchronously on the first
    /// <c>Rendering failed for area</c> record — the host's fault report, written between the scope
    /// probe and the placeholder.
    /// </summary>
    private sealed class FaultLogHook : ILoggerProvider
    {
        private int fired;
        internal Action? OnRenderFaultLogged { get; set; }
        internal int Fired => Volatile.Read(ref fired);

        public ILogger CreateLogger(string categoryName)
            => categoryName == typeof(LayoutAreaHost).FullName ? new HookLogger(this) : NullSink.Instance;

        public void Dispose() { }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();
            public void Dispose() { }
        }

        private sealed class NullSink : ILogger
        {
            internal static readonly NullSink Instance = new();
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => false;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) { }
        }

        private sealed class HookLogger(FaultLogHook owner) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (owner.OnRenderFaultLogged is not { } action)
                    return;
                if (!formatter(state, exception).StartsWith("Rendering failed for area", StringComparison.Ordinal))
                    return;
                if (Interlocked.Exchange(ref owner.fired, 1) == 0)
                    action();
            }
        }
    }
}
