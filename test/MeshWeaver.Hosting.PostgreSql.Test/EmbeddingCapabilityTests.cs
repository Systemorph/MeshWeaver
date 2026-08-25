using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Embeddings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Issue #1642 — <c>search_chunks</c> failing with <i>"The requested service
/// 'MeshWeaver.Hosting.Embeddings.IEmbeddingProvider' has not been registered"</i>.
///
/// <para><b>The verdict this pins: <see cref="IEmbeddingProvider"/> is OPTIONAL.</b> A deployment
/// with no <c>Embedding:Endpoint</c> is a supported deployment — mesh search falls back to an ILIKE
/// substring scan and the content-indexing pipeline resolves its inert stand-ins
/// (<c>ContentIndexingOffToolTest</c> pins that half). So the provider must NOT be required, and the
/// crash the issue reported was correctly fixed by gating the CONSUMER rather than by forcing a
/// registration.</para>
///
/// <para><b>What was still wrong, and is what these tests are for.</b> The decision was taken in
/// silence. <c>TryAddEmbeddingProvider</c> returned a bool one caller read, and nothing in any log
/// said which way it went — so "semantic search is off here" and "semantic search is broken here"
/// produced the same evidence, and #1642 had to be triaged from a stack trace. A capability that
/// degrades silently is indistinguishable from one that is broken. The decision is now an
/// <see cref="EmbeddingCapability"/> in DI plus exactly one startup log line, in BOTH directions —
/// a line that appeared only when the capability was on would be no better than the silence.</para>
///
/// <para>Pure unit tests: no container, no fixture.</para>
/// </summary>
public class EmbeddingCapabilityTests
{
    /// <summary>Runs the real registration and returns what it produced.</summary>
    /// <param name="options">The embedding options to compose with.</param>
    /// <returns>The service collection and whether a provider was registered.</returns>
    private static (IServiceCollection Services, bool Registered) Wire(EmbeddingOptions options)
    {
        var services = new ServiceCollection();
        var registered = services.TryAddEmbeddingProvider(options);
        return (services, registered);
    }

    /// <summary>Resolves the reported capability out of the composed container.</summary>
    /// <param name="services">The composed services.</param>
    /// <returns>The capability.</returns>
    private static EmbeddingCapability Capability(IServiceCollection services)
        => services.BuildServiceProvider().GetRequiredService<EmbeddingCapability>();

    [Fact]
    public void NoEndpoint_LeavesTheProviderUnregistered_AndSaysSo()
    {
        var (services, registered) = Wire(new EmbeddingOptions());

        registered.Should().BeFalse();
        services.Should().NotContain(d => d.ServiceType == typeof(IEmbeddingProvider),
            "an unconfigured deployment registers NO provider — the PRESENCE of the registration is "
            + "the capability signal every consumer reads, so a NullEmbeddingProvider default would "
            + "report a capability this host does not have and light up the indexing pipeline "
            + "against an embedder that can never embed");

        var capability = Capability(services);
        capability.IsEnabled.Should().BeFalse();
        capability.DisabledReason.Should().Contain("Embedding:Endpoint",
            "the reason must name the configuration key that would turn it on — 'disabled' alone "
            + "leaves an operator exactly where #1642 left them");
        capability.Describe().Should().Contain("DISABLED").And.Contain("ILIKE",
            "and must state the CONSEQUENCE, so nobody reads a lexical result set as a data bug");
    }

    [Fact]
    public void CloudBackendWithoutAKey_IsOff_ForTheRightStatedReason()
    {
        var (services, registered) = Wire(new EmbeddingOptions
        {
            Endpoint = "https://example-foundry.invalid",
            // Provider unset ⇒ AzureFoundry, which needs a key.
        });

        registered.Should().BeFalse();
        var capability = Capability(services);
        capability.IsEnabled.Should().BeFalse();
        capability.DisabledReason.Should().Contain("Embedding:ApiKey",
            "an endpoint that is set but keyless is a DIFFERENT fix from an endpoint that is "
            + "absent, and both used to look identical from outside");
        capability.Provider.Should().Be("AzureFoundry");
    }

    [Fact]
    public void ConfiguredLocalBackend_RegistersTheProvider_AndReportsItOn()
    {
        var (services, registered) = Wire(new EmbeddingOptions
        {
            Provider = "Ollama",
            Endpoint = "http://localhost:11434",
            Model = "bge-m3",
        });

        registered.Should().BeTrue();
        services.Should().Contain(d => d.ServiceType == typeof(IEmbeddingProvider));

        var capability = Capability(services);
        capability.IsEnabled.Should().BeTrue();
        capability.DisabledReason.Should().BeNull();
        capability.Dimensions.Should().Be(1024, "bge-m3's known dimensionality");
        capability.Describe().Should().Contain("ENABLED").And.Contain("bge-m3");
    }

    /// <summary>
    /// The capability must never claim more than the host actually did. Re-deriving "on or off"
    /// from the options would be a second copy of <c>CreateEmbeddingProvider</c>'s branch logic,
    /// free to drift and then to advertise a provider that was never registered.
    /// </summary>
    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, "https://example-foundry.invalid", null)]
    [InlineData("Ollama", "http://localhost:11434", null)]
    [InlineData(null, "https://example-foundry.invalid", "key")]
    public void ReportedState_AlwaysMatchesWhetherAProviderWasActuallyRegistered(
        string? provider, string? endpoint, string? apiKey)
    {
        var (services, registered) = Wire(new EmbeddingOptions
        {
            Provider = provider,
            Endpoint = endpoint,
            ApiKey = apiKey,
        });

        Capability(services).IsEnabled.Should().Be(registered);
        services.Any(d => d.ServiceType == typeof(IEmbeddingProvider)).Should().Be(registered);
    }

    /// <summary>
    /// The decision reaches the LOG, not just DI — and in both directions. This runs the real
    /// hosted service the registration installs and captures what it writes.
    /// </summary>
    /// <param name="endpoint">The endpoint to compose with, or null for the unconfigured case.</param>
    /// <param name="expected">A fragment the emitted line must carry.</param>
    [Theory]
    [InlineData(null, "DISABLED")]
    [InlineData("http://localhost:11434", "ENABLED")]
    public async Task TheDecisionIsReportedOnce_AtStartup(string? endpoint, string expected)
    {
        var (services, _) = Wire(new EmbeddingOptions
        {
            Provider = "Ollama",
            Endpoint = endpoint,
            Model = "bge-m3",
        });

        var recorder = new RecordingLoggerProvider();
        services.AddLogging(b => b.AddProvider(recorder).SetMinimumLevel(LogLevel.Information));

        var provider = services.BuildServiceProvider();
        var reporters = provider.GetServices<IHostedService>()
            .OfType<EmbeddingCapabilityReporter>()
            .ToArray();

        reporters.Should().ContainSingle(
            "the decision is announced exactly once per process — repeating it per backend would "
            + "make the banner noise, and omitting it is the silence this fixes");

        await reporters[0].StartAsync(CancellationToken.None);

        recorder.Lines.Should().ContainSingle(line => line.Contains(expected),
            $"an operator must be able to read '{expected}' out of the pod log without a debugger");
    }

    /// <summary>Captures emitted log messages so the startup banner can be asserted on.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> lines = [];

        /// <summary>Everything logged through this provider.</summary>
        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (lines) return lines.ToArray();
            }
        }

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => new Recorder(this);

        /// <inheritdoc />
        public void Dispose() { }

        private void Add(string line)
        {
            lock (lines) lines.Add(line);
        }

        private sealed class Recorder(RecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => owner.Add(formatter(state, exception));
        }
    }
}
