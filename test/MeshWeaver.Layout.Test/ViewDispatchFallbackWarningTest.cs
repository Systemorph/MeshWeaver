using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// The fallback warning of <see cref="LayoutClientConfiguration.GetViewDescriptor"/>: when every
/// registered view map declines a control, ONE Warning on
/// <see cref="LayoutClientConfiguration.ViewDispatchLogCategory"/> names the control, its
/// <c>$type</c>, its skins, the area, the hub and every map that declined by owner.
///
/// <para><b>Why.</b> On memex (2026-09-08) the PartnerRe Workspace area rendered as the TEXT of a
/// <c>StackControl</c> — every map had declined and the escaped-HTML fallback took it — and nothing
/// on that path logged above Debug. A control that turns into text has to say which packs were
/// asked, so the reader can tell "no pack registered on this hub" from "the pack registered and
/// declined this skin".</para>
///
/// <para>Rate-bound per (hub, control type): a page with fifty stacks logs once, and a SECOND type
/// on the same hub logs again — the positive control that proves the single line is a bound, not a
/// dead logger.</para>
/// </summary>
public class ViewDispatchFallbackWarningTest : HubTestBase
{
    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> logRecords = new();

    /// <inheritdoc />
    public ViewDispatchFallbackWarningTest(ITestOutputHelper output) : base(output)
    {
        Services.AddLogging(logging =>
            logging.Services.AddSingleton<ILoggerProvider>(new QueueLoggerProvider(logRecords)));
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private static readonly string TestAssembly = typeof(ViewDispatchFallbackWarningTest).Assembly.GetName().Name!;

    /// <summary>A map that declines everything — the shape of a pack that does not know the control.</summary>
    private static ViewDescriptor? DeclineEverything(object instance, ISynchronizationStream<JsonElement>? stream, string area)
        => null;

    private sealed class ProbeView;

    private IReadOnlyList<string> FallbackWarnings() =>
        logRecords
            .Where(r => r.Category == LayoutClientConfiguration.ViewDispatchLogCategory && r.Level == LogLevel.Warning)
            .Select(r => r.Message)
            .ToList();

    [Fact]
    public void ZeroMaps_LogsTheZeroMapLine_OncePerControlType()
    {
        var client = GetClient();
        var configuration = new LayoutClientConfiguration(client);
        configuration.ViewMapOwners.Should().BeEmpty();

        // Controls.Stack carries a LayoutStackSkin by construction — in its own Skin property, which
        // PrepareForRender merges into Skins before the control leaves the server. The line must
        // name it from EITHER place, so the first call is the unprepared shape and the third the
        // prepared one (skin in both) — and neither lists it twice.
        var descriptor = configuration.GetViewDescriptor(Controls.Stack, null, "Workspace");
        configuration.GetViewDescriptor(Controls.Stack, null, "Workspace");
        configuration.GetViewDescriptor(Controls.Stack.AddSkin(new LayoutStackSkin()).WithStyle("max-width:980px"), null, "Other");

        descriptor.Should().BeNull("there is no map and no fallback");
        var warnings = FallbackWarnings();
        warnings.Should().HaveCount(1, "the same control type on the same hub is logged once");
        warnings[0].Should().Contain("no view map accepted StackControl ($type StackControl, skins [LayoutStackSkin]) in area Workspace on hub " + client.Address);
        warnings[0].Should().Contain("0 map(s) registered — no view pack applied its HubConfigurations to this hub");
        warnings[0].Should().Contain("no fallback view map is set, so the area renders nothing");

        // Positive control for the bound: a DIFFERENT type on the same hub logs again.
        configuration.GetViewDescriptor(Controls.Label("x"), null, "Workspace");
        FallbackWarnings().Should().HaveCount(2);
        FallbackWarnings()[1].Should().Contain("no view map accepted LabelControl ($type LabelControl, skins [])");

        // The prepared shape — skin in Skins AND in the Skin property — on a fresh configuration
        // (a fresh rate-bound) lists the skin exactly once.
        var prepared = new LayoutClientConfiguration(client);
        prepared.GetViewDescriptor(Controls.Stack.AddSkin(new LayoutStackSkin()), null, "Workspace");
        FallbackWarnings().Last().Should().Contain("skins [LayoutStackSkin])");
    }

    [Fact]
    public void DecliningMaps_LogTheirOwners_InRegistrationOrder()
    {
        var client = GetClient();
        var configuration = new LayoutClientConfiguration(client)
            .WithView(DeclineEverything)
            .WithView((_, _, _) => null)
            .WithView<LabelControl, ProbeView>();

        configuration.ViewMapOwners.Should().Equal(
            $"{TestAssembly}:{nameof(DeclineEverything)}",
            // A lambda is attributed to the method that declares it, not to its <…>b__N_M closure name.
            $"{TestAssembly}:{nameof(DecliningMaps_LogTheirOwners_InRegistrationOrder)}",
            $"{TestAssembly}:LabelControl→ProbeView");

        // Anti-vacuity: the typed map DOES answer for its own control, with no warning.
        configuration.GetViewDescriptor(Controls.Label("x"), null, "Workspace")!.Type.Should().Be(typeof(ProbeView));
        FallbackWarnings().Should().BeEmpty();

        configuration.GetViewDescriptor(Controls.Stack, null, "Workspace").Should().BeNull();

        var warnings = FallbackWarnings();
        warnings.Should().HaveCount(1);
        warnings[0].Should().Contain(
            $"3 map(s) registered [{TestAssembly}:{nameof(DeclineEverything)}, "
            + $"{TestAssembly}:{nameof(DecliningMaps_LogTheirOwners_InRegistrationOrder)}, "
            + $"{TestAssembly}:LabelControl→ProbeView]");
    }

    [Fact]
    public void WithFallback_ReturnsTheFallbackDescriptor_AndSaysSo()
    {
        var client = GetClient();
        var configuration = new LayoutClientConfiguration(client)
            .WithView(DeclineEverything)
            .WithFallbackView((_, _, _) => new ViewDescriptor(typeof(ProbeView), new Dictionary<string, object?>()));

        var descriptor = configuration.GetViewDescriptor(Controls.Stack, null, "Workspace");

        descriptor!.Type.Should().Be(typeof(ProbeView));
        var warnings = FallbackWarnings();
        warnings.Should().HaveCount(1);
        warnings[0].Should().Contain("falling back to the last-resort view (escaped HTML in the Blazor portal)");
    }

    /// <summary>
    /// The owners survive the real path: <c>AddViews</c> on the hub configuration, aggregated by the
    /// <see cref="ILayoutClient"/> the renderer resolves — the same object the host's health check reads.
    /// </summary>
    [Fact]
    public void TheLayoutClientsConfiguration_CarriesTheOwners_RegisteredThroughAddViews()
    {
        // GetClient(cfg) REPLACES ConfigureClient rather than extending it, so chain it explicitly.
        var client = GetClient(c => ConfigureClient(c).AddViews(layout => layout.WithView(DeclineEverything)));

        var configuration = client.ServiceProvider.GetRequiredService<ILayoutClient>().Configuration;

        configuration.ViewMapOwners.Should().Equal($"{TestAssembly}:{nameof(DeclineEverything)}");
        configuration.GetViewDescriptor(Controls.Stack, null, "Workspace").Should().BeNull();
        FallbackWarnings().Should().ContainSingle()
            .Which.Should().Contain($"1 map(s) registered [{TestAssembly}:{nameof(DeclineEverything)}]");
    }

    /// <summary>Captures every record, with its category, into the owning test's instance queue.</summary>
    private sealed class QueueLoggerProvider(ConcurrentQueue<(string, LogLevel, string)> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new QueueLogger(categoryName, sink);
        public void Dispose() { }

        private sealed class QueueLogger(string category, ConcurrentQueue<(string, LogLevel, string)> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => sink.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }
}
