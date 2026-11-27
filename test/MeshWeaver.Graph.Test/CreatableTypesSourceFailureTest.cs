using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A create-menu source that misses must not silently erase types.</b> Before this, a slow
/// (15 s) or faulted type query became an empty result with no log line, so the "+ New" menu
/// intermittently dropped types. Now a miss is retried once and, if it still fails, logged as a
/// WARNING naming the source — and the OTHER sources (the static built-ins) are untouched.
/// </summary>
public class CreatableTypesSourceFailureTest
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(150);
    private static readonly JsonSerializerOptions Options = new();

    private sealed class CaptureLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) lock (Warnings) Warnings.Add(formatter(state, exception));
        }
    }

    /// <summary>First <c>failuresBeforeSuccess</c> calls hang (never emit) or throw; later calls answer.</summary>
    private sealed class FlakyQueryCore(int failuresBeforeSuccess, bool hang, params MeshNode[] answer) : IMeshQueryCore
    {
        public int Calls;
        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
        {
            var call = Interlocked.Increment(ref Calls);
            if (call <= failuresBeforeSuccess)
                return hang
                    ? new Subject<QueryResultChange<T>>()
                    : Observable.Throw<QueryResultChange<T>>(new InvalidOperationException("source down"));
            return Observable.Return(new QueryResultChange<T>
            {
                ChangeType = QueryChangeType.Initial,
                Items = answer.Cast<T>().ToArray(),
            });
        }
    }

    private static readonly MeshNode StandardType = new("Markdown", "Acme") { Name = "Markdown", NodeType = MeshNode.NodeTypePath };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATransientMissIsRetried_AndTheTypeIsNotDropped(bool hang)
    {
        var core = new FlakyQueryCore(1, hang, StandardType);
        var log = new CaptureLogger();

        var nodes = await CreatableTypesProvider
            .QueryTypeNodes(core, Options, null, null, log, Short)
            .FirstAsync();

        nodes.Select(n => n.Path).Should().Contain(StandardType.Path,
            "one transient miss must be retried, not turned into an empty result");
        core.Calls.Should().Be(2);
        log.Warnings.Should().ContainSingle(w => w.Contains("type query") || w.Contains("Create menu source"),
            "the miss is logged naming the source, never silent");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APersistentFailureIsLoudAndDoesNotThrow(bool hang)
    {
        var core = new FlakyQueryCore(int.MaxValue, hang);
        var log = new CaptureLogger();

        var nodes = await CreatableTypesProvider
            .QueryTypeNodes(core, Options, null, null, log, Short)
            .FirstAsync();

        nodes.Should().BeEmpty("the dynamic source is gone, the rest of the menu is built elsewhere");
        core.Calls.Should().Be(2, "exactly one retry");
        log.Warnings.Should().Contain(w => w.Contains("WITHOUT it"),
            "falling back is announced, naming the source");
    }

    /// <summary>Negative control: a healthy source logs nothing and is queried once.</summary>
    [Fact]
    public async Task AHealthySourceLogsNothingAndIsNotRetried()
    {
        var core = new FlakyQueryCore(0, false, StandardType);
        var log = new CaptureLogger();

        var nodes = await CreatableTypesProvider
            .QueryTypeNodes(core, Options, null, null, log, Short)
            .FirstAsync();

        nodes.Should().ContainSingle();
        core.Calls.Should().Be(1);
        log.Warnings.Should().BeEmpty();
    }
}
