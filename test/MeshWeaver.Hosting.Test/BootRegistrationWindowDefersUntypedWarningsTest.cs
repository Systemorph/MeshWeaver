using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The boot registration window (Systemorph/MeshWeaver.Plugins#2799).</b> Measured on
/// memex-cloud pod <c>884964bb7-6gv59</c> (2026-10-09): 132 of 134 "stayed an untyped JsonElement"
/// lines were written at 19:48:37.3 — half a second BEFORE the pre-warmer started (19:48:37.8) — by
/// boot-time readers of dynamic types whose assemblies were all on the replica
/// (<c>alreadyBaked=414</c>, <c>compiled=0</c>). The registration-only pass registers those types
/// a few minutes later and every reader re-types; the warning had asserted a verdict ("consumers
/// will fail") that could not yet be decided.
///
/// <para>What is pinned: while the window is open a degraded read is RECORDED (so <c>/health</c>
/// still names it) and writes NO sink record; when the window settles, a type that registered in
/// the meantime warns NEVER, and one that is still untyped warns ONCE, with the same exception
/// marker the CI trace gate keys on. The negative control is the same read with no window open: it
/// warns at the read, exactly as before — which is also every host that does not run the pass.</para>
///
/// <para>No mocking: a real <see cref="MeshContentTypeRegistry"/>, the real production seams
/// (<see cref="MeshNodeStreamCache.ConvertContentJsonElementToTyped"/> and
/// <see cref="MeshNodeStreamCache.DeserializeContent"/>), and the real hosted service driven through
/// its own <c>ApplicationStarted</c> kick.</para>
/// </summary>
public class BootRegistrationWindowDefersUntypedWarningsTest
{
    /// <summary>Stand-in for a dynamically compiled NodeType's content type.</summary>
    public record BootWindowProbeContent(string Title);

    private const string ProbeNodeType = "Probe/BootWindowNodeType";

    private static MeshNode ProbeNode(string id = "AStandingWatch") =>
        new(id, "Ops/Status")
        {
            NodeType = ProbeNodeType,
            Content = JsonSerializer.Deserialize<JsonElement>(
                $$"""{"$type":"{{nameof(BootWindowProbeContent)}}","title":"read at boot"}"""),
        };

    public static TheoryData<string> Seams() => ["GetStream", "GetQuery"];

    private static void Read(
        string seam, MeshNode node, ILogger logger, IMeshContentTypeRegistry registry,
        ContentDegradationRegistry degradations)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _ = seam switch
        {
            "GetStream" => MeshNodeStreamCache.ConvertContentJsonElementToTyped(
                node, options, logger, registry, degradations),
            "GetQuery" => MeshNodeStreamCache.DeserializeContent(node, options, logger, registry, degradations),
            _ => throw new ArgumentOutOfRangeException(nameof(seam), seam, "unknown read seam"),
        };
    }

    /// <summary>The CI trace sink's own predicate (XUnitFileLogger.Log → TestTraceLog.AppendFault).</summary>
    private static bool ReachesTheTraceSink(Record r) => r.Exception is not null && r.Level >= LogLevel.Warning;

    /// <summary>Negative control — no window: the read warns at the read, as it always did.</summary>
    [Theory]
    [MemberData(nameof(Seams))]
    public void WithoutAWindow_ADegradedReadWarnsAtTheRead(string seam)
    {
        var logger = new RecordingLogger();
        var degradations = new ContentDegradationRegistry();

        Read(seam, ProbeNode(), logger, new MeshContentTypeRegistry(), degradations);

        Assert.Single(logger.Records.Where(ReachesTheTraceSink));
        Assert.Single(degradations.Snapshot());
    }

    /// <summary>
    /// The burst's shape: a read of an already-built type during the window, the type registers
    /// afterwards (the registration pass reached it), the window settles — and nothing is ever
    /// warned, because the reader was cured. The read IS recorded while it is untyped, so /health
    /// keeps naming it for exactly as long as it is true.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seams))]
    public void DuringTheWindow_AReadOfATypeThatThenRegisters_IsRecordedAndNeverWarned(string seam)
    {
        var logger = new RecordingLogger();
        var degradations = new ContentDegradationRegistry();
        var registry = new MeshContentTypeRegistry();
        degradations.DeferWarningsUntilRegistrationSettles();

        Read(seam, ProbeNode(), logger, registry, degradations);

        Assert.Empty(logger.Records.Where(ReachesTheTraceSink));
        Assert.Single(degradations.Unresolved(registry));

        // The registration pass reaches the type.
        registry.Register(typeof(BootWindowProbeContent), ProbeNodeType);

        Assert.Empty(degradations.SettleDeferredWarnings(registry));
        Assert.False(degradations.WarningsDeferred);
        Assert.Empty(degradations.Unresolved(registry));
    }

    /// <summary>
    /// The deferral re-times the verdict and never drops it: a type still untyped when the window
    /// settles comes back, once, with every deferred read counted — and a second settle repeats
    /// nothing. After the window, a read warns at the read again.
    /// </summary>
    [Fact]
    public void ATypeStillUntypedWhenTheWindowSettles_IsReturnedOnce_WithEveryRead()
    {
        var logger = new RecordingLogger();
        var degradations = new ContentDegradationRegistry();
        var registry = new MeshContentTypeRegistry();
        degradations.DeferWarningsUntilRegistrationSettles();

        Read("GetStream", ProbeNode("a"), logger, registry, degradations);
        Read("GetQuery", ProbeNode("b"), logger, registry, degradations);
        Assert.Empty(logger.Records.Where(ReachesTheTraceSink));

        var deferred = Assert.Single(degradations.SettleDeferredWarnings(registry));
        Assert.Equal(ProbeNodeType, deferred.NodeType);
        Assert.Equal(2, deferred.Count);
        Assert.Equal("Ops/Status/b", deferred.LastPath);
        Assert.Empty(degradations.SettleDeferredWarnings(registry));

        Read("GetStream", ProbeNode("c"), logger, registry, degradations);
        Assert.Single(logger.Records.Where(ReachesTheTraceSink));
    }

    /// <summary>
    /// End to end through the hosted service: constructing it opens the window, and its kick — here
    /// the "pass disabled" terminal, which settles at once — writes ONE sink-reaching warning for the
    /// still-untyped type, carrying the <see cref="MeshNodeContentDegradedException"/> marker and the
    /// "stayed an untyped JsonElement" phrase that the log queries and the CI gate count.
    /// </summary>
    [Fact]
    public void TheHostedService_OpensTheWindow_AndWritesTheDeferredWarningWhenItSettles()
    {
        var degradations = new ContentDegradationRegistry();
        var registry = new MeshContentTypeRegistry();
        var lifetime = new StartableLifetime();
        var serviceLogger = new RecordingLogger<DynamicContentTypeRegistrationHostedService>();
        using var services = new ServiceCollection()
            .AddSingleton(degradations)
            .AddSingleton<IMeshContentTypeRegistry>(registry)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [DynamicContentTypeRegistrationHostedService.EnabledConfigKey] = "false",
                })
                .Build())
            .BuildServiceProvider();

        using var service = new DynamicContentTypeRegistrationHostedService(services, lifetime, serviceLogger);
        Assert.True(degradations.WarningsDeferred, "constructing the pass's service opens the window");

        Assert.True(service.StartAsync(TestContext.Current.CancellationToken).IsCompletedSuccessfully,
            "StartAsync only registers the ApplicationStarted kick");
        var readLogger = new RecordingLogger();
        Read("GetStream", ProbeNode(), readLogger, registry, degradations);
        Assert.Empty(readLogger.Records.Where(ReachesTheTraceSink));

        lifetime.Start();

        Assert.False(degradations.WarningsDeferred);
        var record = Assert.Single(serviceLogger.Records.Where(ReachesTheTraceSink));
        var marker = Assert.IsType<MeshNodeContentDegradedException>(record.Exception);
        Assert.Equal(ProbeNodeType, marker.NodeType);
        Assert.Equal("Ops/Status/AStandingWatch", marker.NodePath);
        Assert.Contains("stayed an untyped JsonElement", record.Message);
    }

    /// <summary>The registration pass reaches the types readers are waiting on first.</summary>
    [Fact]
    public void ThePass_RegistersTheTypesAReadDegradedFirst_ThenThePathOrder()
    {
        var order = DynamicContentTypeRegistrar.OrderForRegistration(
            ["Crm/Client", "Hosting/DeploymentStatus", "Acme/Thing", "Doc/DataMesh/SocialMedia/Post"],
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
                "Hosting/DeploymentStatus", "doc/datamesh/socialmedia/post"));

        Assert.Equal(
            ["Doc/DataMesh/SocialMedia/Post", "Hosting/DeploymentStatus", "Acme/Thing", "Crm/Client"],
            order.ToArray());
    }

    private sealed record Record(LogLevel Level, string Message, Exception? Exception);

    private class RecordingLogger : ILogger
    {
        private ImmutableList<Record> records = ImmutableList<Record>.Empty;

        public IReadOnlyList<Record> Records => records;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                ImmutableInterlocked.Update(ref records, r => r.Add(new Record(logLevel, formatter(state, exception), exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class RecordingLogger<T> : RecordingLogger, ILogger<T>;

    /// <summary>The host lifetime's ApplicationStarted, fired on demand — the kick the service
    /// registers for. Hosting plumbing, not a mesh interface.</summary>
    private sealed class StartableLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource started = new();

        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void Start() => started.Cancel();

        public void StopApplication() { }
    }
}
