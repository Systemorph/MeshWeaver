using System;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the two halves of Systemorph/MeshWeaver#1684.
///
/// <para><b>(1) The default node chain is applied EXACTLY ONCE per hub.</b>
/// <see cref="MeshNodeHubFactory"/> owns applying <c>MeshConfiguration.DefaultNodeHubConfiguration</c>;
/// every producer of a node's own configuration — notably
/// <see cref="NodeTypeEnrichmentHelpers.WithCompilationErrorOverlay"/> — returns its own delta only.
/// The overlay used to compose the default chain as well, so an overlaid hub ran every
/// <c>ConfigureDefaultNodeHub</c> lambda TWICE. Lambdas that only add views or types absorb that
/// silently; one that contributes a TYPE SOURCE does not — <c>DataContext.Initialize</c> keys
/// <c>TypeSources</c> by collection name, so the second application produced a second
/// <c>Approval</c> collection and <b>hub creation failed outright</b>. Production memex's
/// <c>Doc</c> hub, then <c>Store</c>, <c>Publish/Deck/GateProbe</c> and
/// <c>DoublePendulum/Pendulum/GateProbe</c> in the plugin gate — the victim is simply whichever
/// NodeType happens to take the overlay path.</para>
///
/// <para><b>(2) A genuine duplicate still FAILS — but says what and where.</b> Four separate
/// incident reports had to be reverse-engineered from <c>An item with the same key has already been
/// added. Key: Approval</c>, which names neither the hub nor either contributor. The diagnostic now
/// names the colliding collection, the node whose hub was being created, and both data sources.
/// (Deliberately not a <c>DistinctBy</c> — that would HIDE a real duplicate.)</para>
/// </summary>
public class DefaultNodeChainSingleApplicationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string InstanceId = "overlay-probe";
    private const string MissingNodeType = "No/Such/NodeType";
    private const string FirstContributorId = "first-contributor-data-source";
    private const string SecondContributorId = "second-contributor-data-source";

    /// <summary>The entity the probe lambda contributes a type source for; its name IS the collection name.</summary>
    public record DefaultChainProbe([property: Key] string Id);

    /// <summary>
    /// How often the default node chain ran, per hub address path. INSTANCE state — never static
    /// (AGENTS.md: no static collections); its lifetime is this class's mesh.
    /// </summary>
    private readonly ConcurrentDictionary<string, int> applicationsPerAddress = new();

    /// <summary>Error-level log lines emitted by this mesh — the operator-visible half of the diagnostic.</summary>
    private readonly ConcurrentQueue<string> errorLog = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
                services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(errorLog)))
            .ConfigureDefaultNodeHub(config =>
            {
                applicationsPerAddress.AddOrUpdate(config.Address.Path, 1, (_, count) => count + 1);
                // Deliberately NON-idempotent, in the exact shape of the #1684 victim: a data source
                // whose id is fresh on every application — which is what AddSource's DEFAULT id
                // already is (DataExtensions.DefaultId = a new Guid), so this is not an exotic
                // mistake — contributing a type source for a FIXED collection name. Applied twice,
                // the keep-last-by-id dedupe in DataContext.Initialize can never fire, two type
                // sources claim the 'DefaultChainProbe' collection, and hub creation dies.
                return config.AddData(data =>
                    data.AddSource(source => source.WithType<DefaultChainProbe>()));
            });

    /// <summary>
    /// The compilation-error overlay composed by the real <see cref="IMeshNodeHubFactory"/> — the
    /// exact composition <c>MonolithRoutingService.CreateHub</c> and
    /// <c>MessageHubGrain.ResolveHubConfigurationObservable</c> build — must run the default node
    /// chain once, and the resulting hub must come up.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task OverlaidInstanceHub_RunsTheDefaultNodeChainExactlyOnce()
    {
        // Not persisted on purpose: `CreateNode` refuses an unregistered NodeType, and the
        // composition under test is the hub-configuration chain, which never reads the node row.
        var node = new MeshNode(InstanceId, TestPartition)
        {
            Name = "Overlay probe",
            NodeType = MissingNodeType
        };

        // The shape EnrichWithNodeType produces when a NodeType cannot be resolved.
        var overlaid = NodeTypeEnrichmentHelpers.WithCompilationErrorOverlay(
            node, MissingNodeType, "deliberate: this NodeType has no usable hub configuration");
        overlaid.HubConfiguration.Should().NotBeNull(
            "the overlay always yields a usable hub configuration so the instance still activates");

        var factory = Mesh.ServiceProvider.GetRequiredService<IMeshNodeHubFactory>();
        var enriched = await factory.ResolveHubConfiguration(overlaid).Should().Emit();
        enriched.HubConfiguration.Should().NotBeNull();

        var address = new Address(TestPartition, InstanceId);
        var hub = Mesh.GetHostedHub(address, c => enriched.HubConfiguration!(c));

        hub.Should().NotBeNull(
            "an overlaid instance must still activate — HostedHubsCollection swallows a failed "
            + "creation and returns null, which is exactly how the #1684 hubs went missing");

        applicationsPerAddress.GetValueOrDefault(address.Path).Should().Be(1,
            "MeshNodeHubFactory is the SINGLE owner of the default node chain. Two applications "
            + "double every ConfigureDefaultNodeHub lambda's contribution, and the next one that "
            + "registers a type source fails hub creation with a message that names neither the "
            + "lambda nor the node (#1684)");

        hub!.ServiceProvider.GetRequiredService<IWorkspace>().Should().NotBeNull(
            "resolving IWorkspace forces DataContext.Initialize — a doubled non-idempotent "
            + "contribution threw 'duplicate collection' right here and left the hub uncreatable");
    }

    /// <summary>
    /// A collection genuinely claimed by two data sources must still FAIL — and the diagnostic must
    /// name the collection, the node, and BOTH contributors, so the next report is a one-line
    /// diagnosis instead of a fifth reverse-engineering from a stack trace.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void DuplicateCollection_Diagnostic_NamesCollectionNodeAndBothContributors()
    {
        var address = new Address(TestPartition, "duplicate-collection-diagnostic");

        var hub = Mesh.GetHostedHub(address, config => config.AddData(data => data
            .AddSource(source => source.WithType<DefaultChainProbe>(), id: FirstContributorId)
            .AddSource(source => source.WithType<DefaultChainProbe>(), id: SecondContributorId)));

        hub.Should().BeNull(
            "two data sources claiming one collection is a configuration defect — DataContext must "
            + "FAIL rather than silently dedupe it away (a DistinctBy would hide a real duplicate)");

        var diagnostic = errorLog.FirstOrDefault(line => line.Contains(nameof(DefaultChainProbe)));
        diagnostic.Should().NotBeNull(
            "the duplicate must be reported at Error level — HostedHubsCollection swallows the "
            + "exception, so the log line is all an operator ever sees");
        diagnostic.Should().Contain(nameof(DefaultChainProbe),
            "the diagnostic must name the colliding collection");
        diagnostic.Should().Contain(address.Path,
            "the diagnostic must name the node whose hub was being created — every #1684 report had "
            + "to recover this from the surrounding log lines");
        diagnostic.Should().Contain(FirstContributorId,
            "the diagnostic must name the first contributing data source");
        diagnostic.Should().Contain(SecondContributorId,
            "the diagnostic must name the second contributing data source");
        diagnostic.Should().Contain(typeof(DefaultChainProbe).Assembly.GetName().Name!,
            "the entity type's assembly is the closest the framework can get to naming the module "
            + "that contributed an otherwise anonymous configuration lambda");
    }

    /// <summary>
    /// In-memory Error-level log sink. An instance per mesh (constructed in <c>ConfigureMesh</c>),
    /// writing into a queue owned by the test instance — no static state.
    /// </summary>
    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(sink);
        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                    return;
                var line = formatter(state, exception);
                if (exception is not null)
                    line += Environment.NewLine + exception;
                sink.Enqueue(line);
            }
        }
    }
}
