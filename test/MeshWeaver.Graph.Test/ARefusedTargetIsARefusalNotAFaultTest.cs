using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A move or copy whose target a create VALIDATOR refuses by name is a REFUSAL the caller is
/// answered with — never an Error line</b> (#8126, and the folded fingerprint on #8095).
///
/// <para>Measured on a portal pod: a move of a top-level <c>Space</c> to a path under another
/// partition root was refused by the partition-ownership guard ("a 'Space' owns its partition, so it
/// must be created at the top level"). The refusal is correct — nothing was written at the target,
/// the source was never touched, the reply named the reason — yet the copy handler wrote
/// <c>fail: … Copy {source} -> {target} failed</c> with a stack and the move handler a second
/// <c>fail:</c> line, and the log watch filed both as incidents. #8073 had already settled the same
/// question for a TAKEN target; a target a validator refuses is the same caller error.</para>
///
/// <para><b>SHOULD-FAIL-IF</b> either handler logs a typed validator refusal at Error again. The
/// CONTROL is that the refusal is still SAID — at Warning, naming both paths — so demoting it did not
/// silence it, and the verdict is still the typed <c>ValidationFailed</c>.</para>
/// </summary>
public class ARefusedTargetIsARefusalNotAFaultTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly Lines lines = new();

    /// <summary>Captures every Warning-or-above line from every logger the mesh creates.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<ILoggerProvider>(lines));

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    [Fact(Timeout = 60000)]
    public async Task ACopyOfASpaceUnderAnotherSpace_IsAnsweredAsARefusal_AndLogsNoError()
    {
        var (source, target) = await SeedTwoSpaces();

        var copied = await ObserveNodeOperation(new CopyNodeRequest(source, target))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        Output.WriteLine($"copy: success={copied.Message.Success} reason={copied.Message.RejectionReason} error={copied.Message.Error}");

        copied.Message.Success.Should().BeFalse("a Space owns its partition and cannot be created below the top level");
        copied.Message.RejectionReason.Should().Be(NodeCopyRejectionReason.ValidationFailed);
        AssertRefusedWithoutError(source, target);
    }

    [Fact(Timeout = 60000)]
    public async Task AMoveOfASpaceUnderAnotherSpace_IsAnsweredAsARefusal_AndLogsNoError()
    {
        var (source, target) = await SeedTwoSpaces();

        var moved = await ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        Output.WriteLine($"move: success={moved.Message.Success} reason={moved.Message.RejectionReason} error={moved.Message.Error}");

        moved.Message.Success.Should().BeFalse("a Space owns its partition and cannot be created below the top level");
        moved.Message.RejectionReason.Should().Be(NodeMoveRejectionReason.ValidationFailed);
        AssertRefusedWithoutError(source, target);
    }

    private async Task<(string Source, string Target)> SeedTwoSpaces()
    {
        Access.SetCircuitContext(TestUsers.Admin);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = $"movedspace{suffix}";
        var home = $"homespace{suffix}";
        foreach (var id in new[] { source, home })
            await SeedTopLevel(new MeshNode(id)
            {
                Name = id,
                NodeType = SpaceNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new Space(),
            });
        return (source, $"{home}/{source}");
    }

    private void AssertRefusedWithoutError(string source, string target)
    {
        bool NamesBoth(string line) =>
            line.Contains(source, StringComparison.Ordinal) && line.Contains(target, StringComparison.Ordinal);
        var all = lines.Captured;
        foreach (var (level, text) in all.Where(l => NamesBoth(l.Text)))
            Output.WriteLine($"{level}: {text}");

        all.Where(l => l.Level >= LogLevel.Error && NamesBoth(l.Text)).Should().BeEmpty(
            "a target a validator refuses is the caller's answer, not a fault — the reply already names it");
        all.Where(l => l.Level == LogLevel.Warning && NamesBoth(l.Text) && l.Text.Contains("refused", StringComparison.Ordinal))
            .Should().NotBeEmpty("CONTROL — the refusal is still said, at Warning, naming both paths");
    }

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
