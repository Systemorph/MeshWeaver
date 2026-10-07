using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.TypeSystem;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 Issue #6189 — a rolling update puts TWO platform generations into ONE Orleans cluster for the
/// whole drain window, and "everything should be compatible": a silo of the current generation and a
/// silo of the previous one must still learn each other's HOSTED TYPES (the grain manifest every
/// placement decision reads), and the current one must never send the previous one a request it
/// cannot decode.
///
/// <para><b>The incident.</b> The 2026-10-06 roll on memex crossed Orleans 10.3.1 → 10.4.0. 10.4.0
/// added three <c>IClusterManifestSystemTarget</c> methods (content-addressed manifest retrieval,
/// aliases <c>3D9B7FE6</c>, <c>93B8854F</c>, <c>25AE6E4A</c>) and turned them on by default. Every
/// manifest refresh a 10.4 silo made against a 10.3 silo was rejected in
/// <c>Connection.ProcessIncoming</c> with <c>TypeLoadException: Unable to resolve type alias</c> — 121
/// errors on 11 receiving pods in one hour. The 10.4 caller then fell back to the legacy
/// <c>ISiloManifestSystemTarget.GetSiloManifest</c> (alias <c>1857A4C8</c>, known to every 10.x silo),
/// so the manifest still arrived; the defect was the new generation speaking a dialect its
/// predecessor cannot read.</para>
///
/// <para><b>The model of the skew (controlled switches).</b> One process cannot load two Orleans
/// versions, so the secondary silo is made the PREVIOUS generation the way that generation differs on
/// the wire: its serializer has no invokable for the three aliases 10.3.1 never had (stripped from its
/// <see cref="TypeManifestOptions.CompoundTypeAliases"/>), and it retrieves manifests directly, as
/// 10.3.1 always did. The primary silo is the CURRENT generation built by the production
/// <c>ConfigureMeshWeaverServer</c>. Each case proves its switch took — a skew that silently did not
/// apply would make the clean case vacuous — by the incident's own error appearing in the case that
/// re-enables Orleans' default.</para>
///
/// <para><b>Negative controls.</b> (1) <see cref="WithOrleansDefault_ThePreviousGenerationRejectsTheManifestRequest"/>
/// forces <c>EnableContentAddressedRetrieval = true</c> on the current silo — the configuration before
/// the fix — and reproduces the incident's exact rejection. Reverting the one production line makes the
/// clean case fail the same way. (2) <see cref="WithoutTheLegacyPath_TheCurrentGenerationNeverLearnsThePreviousOnesHostedTypes"/>
/// also strips the legacy alias, so no common protocol remains, and shows the interop assertion fails
/// when the generations genuinely cannot talk.</para>
/// </summary>
public class CrossGenerationManifestExchangeTest(ITestOutputHelper output)
{
    /// <summary>The aliases Orleans 10.4.0 added to <c>IClusterManifestSystemTarget</c> (measured by diffing
    /// the generated invokables of Orleans.Core / Orleans.Runtime 10.3.1 against 10.4.0 — these three are the
    /// only additions).</summary>
    private static readonly ImmutableArray<string> AliasesThePreviousGenerationLacks = ["3D9B7FE6", "93B8854F", "25AE6E4A"];

    /// <summary>The legacy direct-retrieval alias every 10.x silo decodes.</summary>
    private const string LegacySiloManifestAlias = "1857A4C8";

    private const string PrimarySiloName = "Primary";

    [Fact(Timeout = 240_000)]
    public async Task ACurrentAndAPreviousGenerationSilo_LearnEachOthersHostedTypes_WithNoRejectedRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = await GenerationSkewCluster.Deploy(new Skew(PreviousGenerationLacksLegacyPath: false, ForceOrleansDefaultOnCurrent: false), output);
        try
        {
            await cluster.EachSiloKnowsTheOther(ct);

            cluster.RejectedOnPrevious.Should().BeEmpty(
                "the current generation must never send the previous generation a request it cannot decode — "
                + "every rejection here is one 'Unable to resolve type alias' line on a live portal during a roll (#6189)");
        }
        finally
        {
            cluster.Dispose();
        }
    }

    [Fact(Timeout = 240_000)]
    public async Task WithOrleansDefault_ThePreviousGenerationRejectsTheManifestRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = await GenerationSkewCluster.Deploy(new Skew(PreviousGenerationLacksLegacyPath: false, ForceOrleansDefaultOnCurrent: true), output);
        try
        {
            // Orleans' own fallback still lands the manifest — the incident was noise and a
            // failed round trip per refresh, never a lost manifest.
            await cluster.EachSiloKnowsTheOther(ct);

            cluster.RejectedOnPrevious.Should().Contain(line => line.Contains("Unable to resolve type alias", StringComparison.Ordinal)
                    && AliasesThePreviousGenerationLacks.Any(a => line.Contains(a, StringComparison.Ordinal)),
                "with content-addressed retrieval on, the current generation asks the previous one for a manifest hash it has "
                + "no invokable for — the incident's exact error. If this stops reproducing, the skew model no longer "
                + "applies and the clean case above measures nothing");
        }
        finally
        {
            cluster.Dispose();
        }
    }

    [Fact(Timeout = 240_000)]
    public async Task WithoutTheLegacyPath_TheCurrentGenerationNeverLearnsThePreviousOnesHostedTypes()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = await GenerationSkewCluster.Deploy(new Skew(PreviousGenerationLacksLegacyPath: true, ForceOrleansDefaultOnCurrent: false), output);
        try
        {
            // Positive anchor: the previous generation has received — and rejected — the direct
            // manifest request, so the skew applied and the current silo has tried to learn it.
            await cluster.RejectionsOnPrevious
                .Where(line => line.Contains(LegacySiloManifestAlias, StringComparison.Ordinal))
                .Should().Within(TestTimeouts.CrossSilo)
                .Emit("the previous generation receives — and cannot decode — the direct manifest request", ct);

            // The rejection is logged BEFORE the error response is sent, so a single read here would
            // be false by construction (it is false from startup). Re-read over the SAME bound the
            // clean case waits for the opposite answer: only silence across that whole window shows
            // the clean case's wait would really fail, rather than a later refresh or fallback
            // publishing the manifest after a premature sample.
            await Observable.Interval(TimeSpan.FromMilliseconds(100))
                .StartWith(0L)
                .Where(_ => cluster.CurrentKnowsPrevious())
                .Should().NotEmit(TestTimeouts.CrossSilo,
                    "with no protocol in common the current generation cannot learn the previous one's hosted types — "
                    + "the failure the clean case would report if Orleans ever dropped the legacy path", ct);
        }
        finally
        {
            cluster.Dispose();
        }
    }

    /// <summary>The controlled switches of one cluster.</summary>
    /// <param name="PreviousGenerationLacksLegacyPath">Also strip the legacy direct-retrieval alias from the
    /// previous generation, leaving no common protocol (negative control for the interop assertion).</param>
    /// <param name="ForceOrleansDefaultOnCurrent">Force Orleans' default (content-addressed retrieval ON) on
    /// the current generation — the configuration before #6189's fix (negative control for the fix).</param>
    private sealed record Skew(bool PreviousGenerationLacksLegacyPath, bool ForceOrleansDefaultOnCurrent);

    /// <summary>
    /// A two-silo cluster: "Primary" is the current generation (production silo configuration), the other
    /// silo the previous generation as <see cref="Skew"/> describes.
    /// </summary>
    private sealed class GenerationSkewCluster : IDisposable
    {
        private readonly RejectionCapture capture = new();
        private OrleansTestClusterHost host = null!;

        public IReadOnlyCollection<string> RejectedOnPrevious => capture.Lines;
        public IObservable<string> RejectionsOnPrevious => capture.Stream;

        public static async Task<GenerationSkewCluster> Deploy(Skew skew, ITestOutputHelper output)
        {
            var cluster = new GenerationSkewCluster();
            cluster.host = await OrleansTestCluster.DeployAsync(
                builder =>
                {
                    builder.Options.InitialSilosCount = 2;
                    builder.Options.SiloBuilderConfiguratorTypes.Add(typeof(TwoSiloConfigurator).AssemblyQualifiedName!);
                    builder.CreateSiloAsync = async (siloName, configuration) =>
                        await InProcessSiloHandle.CreateAsync(
                            siloName,
                            configuration,
                            hostBuilder => hostBuilder.ConfigureServices(services =>
                            {
                                if (siloName == PrimarySiloName)
                                {
                                    if (skew.ForceOrleansDefaultOnCurrent)
                                        services.PostConfigure<ClusterManifestOptions>(o => o.EnableContentAddressedRetrieval = true);
                                    return;
                                }
                                var stripped = skew.PreviousGenerationLacksLegacyPath
                                    ? AliasesThePreviousGenerationLacks.Add(LegacySiloManifestAlias)
                                    : AliasesThePreviousGenerationLacks;
                                services.PostConfigure<TypeManifestOptions>(o =>
                                    StripInvokables(o, StripAliases(o.CompoundTypeAliases, stripped)));
                                // The previous generation retrieves manifests directly — 10.3.1 had no other path.
                                services.PostConfigure<ClusterManifestOptions>(o => o.EnableContentAddressedRetrieval = false);
                                services.AddSingleton<ILoggerProvider>(cluster.capture);
                            }));
                },
                withClient: false);
            output.WriteLine($"[skew] {skew}; silos: {string.Join(", ", cluster.host.Cluster.Silos.Select(s => s.Name))}");
            return cluster;
        }

        private IServiceProvider Services(bool current) =>
            ((InProcessSiloHandle)host.Cluster.Silos.Single(s => (s.Name == PrimarySiloName) == current)).SiloHost.Services;

        private static SiloAddress AddressOf(IServiceProvider silo) => silo.GetRequiredService<ILocalSiloDetails>().SiloAddress;

        private static bool Knows(IServiceProvider silo, IServiceProvider other) =>
            silo.GetRequiredService<IClusterManifestProvider>().Current.Silos.ContainsKey(AddressOf(other));

        public bool CurrentKnowsPrevious() => Knows(Services(current: true), Services(current: false));

        public async Task EachSiloKnowsTheOther(System.Threading.CancellationToken ct)
        {
            var current = Services(current: true);
            var previous = Services(current: false);
            // The manifest provider exposes no observable, only Current — so the wait re-reads it.
            await Observable.Interval(TimeSpan.FromMilliseconds(100))
                .StartWith(0L)
                .Where(_ => Knows(current, previous) && Knows(previous, current))
                .Should().Within(TestTimeouts.CrossSilo)
                .Emit("each generation's cluster manifest must hold the other's hosted types — "
                      + "without it no grain is placed on, or routed to, the other generation", ct);
        }

        public void Dispose() => OrleansClusterDisposal.DisposeInBackground(host);

        /// <summary>
        /// Removes the given method aliases under <c>("inv", GrainReference, IClusterManifestSystemTarget|ISiloManifestSystemTarget)</c>
        /// — the receive side of the previous generation. Asserts each one was present: a strip that matched
        /// nothing would leave the "previous" silo current and every case vacuous.
        /// </summary>
        private static ImmutableHashSet<Type> StripAliases(CompoundTypeAliasTree root, IReadOnlyCollection<string> aliases)
        {
            var children = typeof(CompoundTypeAliasTree).GetField("_children", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("CompoundTypeAliasTree._children not found — the Orleans layout changed; re-derive the skew model.");
            Dictionary<object, CompoundTypeAliasTree> Of(CompoundTypeAliasTree node) =>
                (Dictionary<object, CompoundTypeAliasTree>?)children.GetValue(node)
                ?? throw new InvalidOperationException($"alias tree node '{node.Key}' has no children");

            var grainReference = Of(Of(root)["inv"])[typeof(GrainReference)];
            var targets = Of(grainReference)
                .Where(kv => kv.Key is Type t && t.Name is "IClusterManifestSystemTarget" or "ISiloManifestSystemTarget")
                .Select(kv => Of(kv.Value))
                .ToArray();
            targets.Should().HaveCount(2, "both manifest system targets must be in the alias tree for the skew to be modelled");
            var invokables = ImmutableHashSet<Type>.Empty;
            foreach (var alias in aliases)
            {
                var holders = targets.Where(t => t.ContainsKey(alias)).ToArray();
                holders.Should().HaveCount(1, $"alias {alias} must exist exactly once to be stripped");
                invokables = invokables.Add(holders[0][alias].Value
                    ?? throw new InvalidOperationException($"alias {alias} names no invokable type"));
                holders[0].Remove(alias);
            }
            return invokables;
        }

        /// <summary>
        /// Removes every codec, copier and activator of the stripped invokables — the previous generation
        /// never compiled them. (Leaving them registered with no alias makes the serializer refuse to start:
        /// it formats every registered type at construction.)
        /// </summary>
        private static void StripInvokables(TypeManifestOptions options, IReadOnlySet<Type> invokables)
        {
            bool Serves(Type registered) => registered.GetInterfaces()
                .Any(i => i.IsGenericType && i.GetGenericArguments().Any(invokables.Contains));
            var removed = 0;
            foreach (var set in new[] { options.Serializers, options.FieldCodecs, options.Copiers, options.Activators, options.Converters })
                removed += set.RemoveWhere(Serves);
            removed.Should().BeGreaterThanOrEqualTo(invokables.Count,
                "each stripped invokable has at least a codec — removing none means the skew was not applied");
        }
    }

    /// <summary>
    /// Captures <c>Orleans.Runtime.Messaging.Connection</c> errors on the previous-generation silo — the
    /// incident's log site. An instance owned by its cluster, never static.
    /// </summary>
    private sealed class RejectionCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> lines = new();
        private readonly System.Reactive.Subjects.ReplaySubject<string> stream = new();

        public IReadOnlyCollection<string> Lines => lines.ToArray();
        public IObservable<string> Stream => stream.AsObservable();

        public ILogger CreateLogger(string categoryName) =>
            categoryName == "Orleans.Runtime.Messaging.Connection" ? new Logger(this) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose() { }

        private void Add(string line)
        {
            lines.Enqueue(line);
            stream.OnNext(line);
        }

        private sealed class Logger(RejectionCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error)
                    owner.Add($"{formatter(state, exception)} | {exception?.GetType().Name}: {exception?.Message}");
            }
        }
    }
}
