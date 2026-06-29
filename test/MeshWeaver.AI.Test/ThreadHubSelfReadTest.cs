using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Regression repro for the atioz <c>get @Thread</c> failure. The <c>Thread</c> NodeType node is a real
/// per-node hub (its <c>NodeTypeDefinition</c> is not definition-only) that runs thread execution; on
/// cold activation its <c>SetThreadHubIdentity</c> initializer self-read its own node via
/// <c>hub.GetMeshNode(hub.Address.ToString())</c>. The old <c>GetMeshNode</c> posted a
/// <c>GetDataRequest</c> to the hub's OWN address before any identity was established, which
/// <c>PostPipeline</c> failed closed with "AccessContext must never be null ... hub=Thread,
/// GetDataRequest, target=Thread". <c>GetMeshNode</c> now reads the own node off the local
/// <c>MeshNodeReference</c> reducer (no post), so activating the hub must NOT log that error.
/// </summary>
public class ThreadHubSelfReadTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly ConcurrentQueue<string> _accessContextErrors = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            // AddAI wires the Thread NodeType's thread-execution HubConfiguration (SetThreadHubIdentity),
            // i.e. the cold-activation self-read that this test pins.
            .AddAI()
            .ConfigureServices(s => s.AddSingleton<ILoggerProvider>(
                new CapturingLoggerProvider(_accessContextErrors)));

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Activating_thread_node_hub_does_not_self_post_a_contextless_GetDataRequest()
    {
        // Reading @Thread activates its per-node hub; the SetThreadHubIdentity initializer self-reads it.
        var node = await ReadNode("Thread").FirstAsync().ToTask(Ct);
        Assert.NotNull(node);

        // Negative assertion (sanctioned fixed wait -- "confirm nothing happened"): let the cold-activation
        // self-read run, then confirm it did NOT trip the never-null guard with a self GetDataRequest.
        await Task.Delay(1000, Ct);

        var offending = _accessContextErrors
            .Where(m => m.Contains("GetDataRequest") && m.Contains("target=Thread"))
            .ToList();
        Assert.True(offending.Count == 0,
            "The thread hub must read its own node via the local reducer (GetMeshNodeStream), never a " +
            "self-posted GetDataRequest. Offending AccessContext errors: " + string.Join(" | ", offending));
    }

    /// <summary>Captures error-level messages on the <c>MeshWeaver.AccessContext</c> channel.</summary>
    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) =>
            categoryName.Contains("AccessContext", StringComparison.OrdinalIgnoreCase)
                ? new CapturingLogger(sink)
                : NullLogger.Instance;

        public void Dispose() { }
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                sink.Enqueue(formatter(state, exception));
        }
    }
}
