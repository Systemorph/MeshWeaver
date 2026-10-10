using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// MeshWeaver#6391 — the post-creation verdict is said ONCE, and the line and the reply agree.
///
/// <para><b>The race.</b> The deadline used to read the once-only gate, write its Error line and
/// only then claim the answer. A post-creation leg finishing between the line and the claim answered
/// <c>Ok</c> under a line that says 'outcome unknown'. The test makes that window deterministic: the
/// leg is parked inside a handler, and it is released from INSIDE the write of the Error line — the
/// exact point between the old read and the old claim — and given time to finish and answer before
/// the write returns. With the claim made first, the finishing leg's <c>Ok</c> is refused and the
/// reply carries the verdict the line states. With the old order the reply is <c>Ok</c>.</para>
///
/// <para><b>The step.</b> The handler parks while its additional nodes are being DISCOVERED
/// (<c>GetAdditionalNodes</c>), which runs before <c>Handle</c> is subscribed. The verdict names
/// that step, never <c>Handle</c>.</para>
/// </summary>
public class PostCreationVerdictIsSaidOnceTest : MonolithMeshTestBase
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan Window = Budget + TimeSpan.FromSeconds(12);

    /// <summary>How long the Error line's write gives a released leg to finish and answer.</summary>
    private static readonly TimeSpan RaceWindow = TimeSpan.FromSeconds(3);

    private readonly ParkingPostCreationHandler handler = new();
    private readonly VerdictLine line = new();

    public PostCreationVerdictIsSaidOnceTest(ITestOutputHelper output) : base(output)
        // After the base constructor's ClearProviders(); the verdict is an Error line, so no level changes.
        => Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(line));

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services
            .AddSingleton<INodePostCreationHandler>(handler)
            .AddSingleton(new MeshOperationOptions { Timeout = Budget }));

    [Fact(Timeout = 90000)]
    public async Task ALegFinishingWhileTheVerdictIsBeingSaid_CannotAnswerOk_AndTheVerdictNamesTheStep()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/said-once-{Guid.NewGuid().ToString("N")[..8]}";
        handler.Park(path);
        Task<CreateNodeResponse>? pending = null;
        // Runs INSIDE the write of the Error line, on the deadline's thread: release the parked leg
        // and wait for it to answer. It can only answer if the deadline has not claimed yet.
        line.OnVerdict(path, () =>
        {
            handler.Release();
            SpinWait.SpinUntil(() => Volatile.Read(ref pending) is { IsCompleted: true }, RaceWindow);
        });
        try
        {
            pending = ObserveNodeOperation(new CreateNodeRequest(MeshNode.FromPath(path) with
                {
                    Name = "said once", NodeType = "Markdown", State = MeshNodeState.Active,
                }))
                .Select(d => d.Message)
                .FirstAsync().Await(ct);

            var response = await pending.WaitAsync(Window, ct);
            Output.WriteLine($"LINE:  {line.Said}");
            Output.WriteLine($"REPLY: success={response.Success} error={response.Error}");

            line.Said.Should().NotBeNull("the deadline wrote its verdict while the leg was parked");
            response.Success.Should().BeFalse(
                "the line says 'outcome unknown', so that is the answer: a leg that finishes while the "
                + "line is being written must not answer Ok under it");
            line.Said.Should().Contain($"{nameof(ParkingPostCreationHandler)} (");
            line.Said.Should().Contain(": discovering additional nodes",
                "the leg was parked in GetAdditionalNodes — Handle had not even been subscribed");
            response.RejectionReason.Should().Be(NodeCreationRejectionReason.Unavailable);
            response.Error.Should().Contain(": discovering additional nodes",
                "the reply carries the same verdict the line states");
            handler.Discovered.Should().BeTrue("the released leg really did finish inside the window");
        }
        finally
        {
            handler.Release();
        }
    }

    /// <summary>Parks inside <c>GetAdditionalNodes</c> for the path it was told to. Instance state.</summary>
    private sealed class ParkingPostCreationHandler : INodePostCreationHandler
    {
        private string? parked;
        private int released;
        private int discovered;

        public void Park(string path) => Volatile.Write(ref parked, path);

        public void Release() => Volatile.Write(ref released, 1);

        public bool Discovered => Volatile.Read(ref discovered) == 1;

        public string NodeType => "Markdown";

        public IObservable<System.Reactive.Unit> Handle(MeshNode createdNode, string? createdBy)
            => Observable.Empty<System.Reactive.Unit>();

        public IEnumerable<MeshNode> GetAdditionalNodes(MeshNode createdNode)
        {
            if (string.Equals(createdNode.Path, Volatile.Read(ref parked), StringComparison.OrdinalIgnoreCase))
            {
                SpinWait.SpinUntil(() => Volatile.Read(ref released) == 1, TimeSpan.FromSeconds(45));
                Volatile.Write(ref discovered, 1);
            }
            return [];
        }
    }

    /// <summary>Records the post-creation verdict line for one path and runs a hook inside its write.</summary>
    private sealed class VerdictLine : ILoggerProvider
    {
        private string? path;
        private Action? hook;
        private string? said;

        public string? Said => Volatile.Read(ref said);

        public void OnVerdict(string watchedPath, Action inside)
        {
            Volatile.Write(ref hook, inside);
            Volatile.Write(ref path, watchedPath);
        }

        public ILogger CreateLogger(string categoryName) => new Capturing(this);

        public void Dispose() { }

        private void Offer(string message)
        {
            if (Volatile.Read(ref path) is not { } watched
                || !message.Contains(watched, StringComparison.Ordinal)
                || !message.Contains("its post-creation handlers had not finished", StringComparison.Ordinal))
                return;
            Volatile.Write(ref said, message);
            Interlocked.Exchange(ref hook, null)?.Invoke();
        }

        private sealed class Capturing(VerdictLine owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error)
                    owner.Offer(formatter(state, exception));
            }
        }
    }
}
