using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A move or copy onto a path that is already taken is a REFUSAL the caller is answered with —
/// never an Error line</b> (#8073).
///
/// <para>Measured on a portal pod: three moves of document nodes into a space whose same-named paths
/// were already occupied each wrote <c>fail: … Copy {source} -> {target} failed</c> with a stack
/// (<c>InvalidOperationException: Node already exists</c>) at the copy handler, plus a second
/// <c>fail:</c> line at the move handler — and the log watch filed them as an incident. Nothing was
/// wrong: the refusal is correct, nothing was written or removed, and the reply named the reason
/// (<see cref="NodeMoveRejectionReason.TargetAlreadyExists"/>). A plain create onto an occupied path
/// logs nothing at all; a copy or a move onto one is the same caller error.</para>
///
/// <para><b>SHOULD-FAIL-IF</b> either handler logs a typed collision at Error again. The CONTROL is
/// that the refusal is still SAID — at Warning, naming both paths — so demoting it did not silence it.</para>
/// </summary>
public class ATakenTargetIsARefusalNotAFaultTest : MonolithMeshTestBase
{
    private readonly Lines lines = new();

    /// <summary>Captures every Warning-or-above line from every logger the mesh creates.</summary>
    public ATakenTargetIsARefusalNotAFaultTest(ITestOutputHelper output) : base(output)
        => Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(lines));

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    [Fact(Timeout = 60000)]
    public async Task AMoveOntoATakenPath_IsAnsweredAsARefusal_AndLogsNoError()
    {
        Access.SetCircuitContext(TestUsers.Admin);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/document-{suffix}";
        var target = $"{TestPartition}/taken-{suffix}";
        await Seed(source, "Document");
        await Seed(target, "Someone else's document");

        var moved = await ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        Output.WriteLine($"move: success={moved.Message.Success} reason={moved.Message.RejectionReason} error={moved.Message.Error}");

        moved.Message.Success.Should().BeFalse("the target holds a different node");
        moved.Message.RejectionReason.Should().Be(NodeMoveRejectionReason.TargetAlreadyExists);
        AssertRefusedWithoutError(source, target);
    }

    [Fact(Timeout = 60000)]
    public async Task ACopyOntoATakenPath_IsAnsweredAsARefusal_AndLogsNoError()
    {
        Access.SetCircuitContext(TestUsers.Admin);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/document-{suffix}";
        var target = $"{TestPartition}/taken-{suffix}";
        await Seed(source, "Document");
        await Seed(target, "Someone else's document");

        var copied = await ObserveNodeOperation(new CopyNodeRequest(source, target))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        Output.WriteLine($"copy: success={copied.Message.Success} reason={copied.Message.RejectionReason} error={copied.Message.Error}");

        copied.Message.Success.Should().BeFalse("the target holds a different node");
        copied.Message.RejectionReason.Should().Be(NodeCopyRejectionReason.TargetAlreadyExists);
        AssertRefusedWithoutError(source, target);
    }

    private void AssertRefusedWithoutError(string source, string target)
    {
        bool NamesBoth(string line) =>
            line.Contains(source, StringComparison.Ordinal) && line.Contains(target, StringComparison.Ordinal);
        var all = lines.Captured;
        foreach (var (level, text) in all.Where(l => NamesBoth(l.Text)))
            Output.WriteLine($"{level}: {text}");

        all.Where(l => l.Level >= LogLevel.Error && NamesBoth(l.Text)).Should().BeEmpty(
            "a taken target is the caller's answer, not a fault — the reply already names it");
        all.Where(l => l.Level == LogLevel.Warning && NamesBoth(l.Text) && l.Text.Contains("refused", StringComparison.Ordinal))
            .Should().NotBeEmpty("CONTROL — the refusal is still said, at Warning, naming both paths");
    }

    private async Task Seed(string path, string name) =>
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = name,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

    private sealed class Lines : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Text)> lines = new();

        public (LogLevel Level, string Text)[] Captured => lines.ToArray();

        public ILogger CreateLogger(string categoryName) => new Capturing(this, categoryName);

        public void Dispose() { }

        private sealed class Capturing(Lines owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                    owner.lines.Enqueue((logLevel, $"{category}: {formatter(state, exception)}"));
            }
        }
    }
}
