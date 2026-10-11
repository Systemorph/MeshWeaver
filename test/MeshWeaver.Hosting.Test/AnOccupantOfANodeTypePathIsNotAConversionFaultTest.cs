using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The occupant of a path that a read names as a NodeType is NOT a failed conversion
/// (Systemorph/MeshWeaver#6525, Doc/Architecture/DanglingNodeTypes hole C, #5264).
///
/// <para>A read of content typed with a bare path (the incident: <c>Feedback</c>, where the Store
/// plugin ROOT sits and its declaration is at <c>Feedback/Feedback</c>) asks
/// <see cref="ContentTypeOnDemandRegistration"/> to register that path. The store answers with the
/// OCCUPANT, typed as something other than a NodeTypeDefinition. Converting it with the logger
/// attached published the bare, subject-free
/// <c>As&lt;NodeTypeDefinition&gt; for Feedback: value is PluginContent ... not convertible</c> line at
/// Error. The occupant here is a node of NodeType <c>Code</c> carrying <see cref="CodeConfiguration"/>
/// - provably not a declaration, exactly the shape the plugin root has.</para>
///
/// <para>The control on the other side: a node that DECLARES itself a NodeType but whose content
/// does not convert is a genuine fault and must stay loud.</para>
/// </summary>
public class AnOccupantOfANodeTypePathIsNotAConversionFaultTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private MeshNode Typed(string nodeType, string id) => new(id, TestPartition)
    {
        Name = id,
        NodeType = nodeType,
        State = MeshNodeState.Active,
        Content = new CodeConfiguration
        {
            Language = "csharp",
            Code = "public record NotADeclaration;",
        },
    };

    private async Task Store(MeshNode node, CancellationToken cancellationToken)
    {
        await Storage.Write(node, Mesh.JsonSerializerOptions).Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit($"{node.Path} lands in the store", cancellationToken);
        var stored = await Storage.Read(node.Path, Mesh.JsonSerializerOptions).Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit($"{node.Path} is readable from the store", cancellationToken);
        stored.Should().NotBeNull();
        stored!.Content.Should().BeOfType<CodeConfiguration>(
            "the control: the row reads back TYPED, so the conversion sees a typed non-declaration "
            + "(an untyped JsonElement would convert quietly and the test would measure nothing)");
    }

    /// <summary>
    /// THE REPRODUCTION. On the unchanged call site the capturing logger receives
    /// <c>As&lt;NodeTypeDefinition&gt; for {path}: value is CodeConfiguration (...), not convertible</c>
    /// at Error and the pre-warmer's untyped-node Warning; with the one-sided test applied first
    /// the occupant is answered NotBaked, named, and nothing is logged at Warning or above.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ANonDeclarationOccupant_IsAnsweredNotBaked_WithoutAFailedConversionLine()
    {
        var ct = TestContext.Current.CancellationToken;
        var occupant = Typed("Code", $"Occ{suffix}");
        await Store(occupant, ct);

        var logger = new CapturingLogger();
        var outcome = await new ContentTypeOnDemandRegistration(logger)
            .EnsureRegistered(Mesh, occupant.Path)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the on-demand attempt reaches exactly one outcome", ct);

        foreach (var (level, message) in logger.Entries)
            Output.WriteLine($"{level}: {message}");

        outcome.Status.Should().Be(ContentTypeRegistrationStatus.NotBaked, outcome.Detail ?? "(no detail)");
        outcome.Detail.Should().Contain(occupant.Path, "the verdict names the occupant that is in the way")
            .And.Contain("Code", "and says what it is");

        var faults = logger.Entries
            .Where(e => e.Level >= LogLevel.Warning)
            .Select(e => e.Message)
            .ToList();
        faults.Should().BeEmpty(
            "a node that is PROVABLY not a NodeType declaration is the normal occupant of a path a read "
            + "names as a type - answering it must not publish a failed As<NodeTypeDefinition> "
            + "conversion (#6525)");
    }

    /// <summary>
    /// THE CONTROL ON THE OTHER SIDE. A node that declares itself a NodeType (NodeType = "NodeType")
    /// is never convicted by the one-sided test, so when its content does not convert the conversion
    /// still runs with the logger and the fault is still reported, naming the path.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ADeclaredNodeTypeThatDoesNotConvert_IsStillReported()
    {
        var ct = TestContext.Current.CancellationToken;
        var broken = Typed(MeshNode.NodeTypePath, $"Broken{suffix}");
        await Store(broken, ct);

        var logger = new CapturingLogger();
        await new ContentTypeOnDemandRegistration(logger)
            .EnsureRegistered(Mesh, broken.Path)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the on-demand attempt reaches exactly one outcome", ct);

        foreach (var (level, message) in logger.Entries)
            Output.WriteLine($"{level}: {message}");

        var errors = logger.Entries
            .Where(e => e.Level == LogLevel.Error)
            .Select(e => e.Message)
            .ToList();
        errors.Any(m => m.Contains("As<NodeTypeDefinition>") && m.Contains(broken.Path))
            .Should().BeTrue(
                "a declared NodeType whose content does not convert is a genuine fault and must stay loud");
    }

    private sealed class CapturingLogger : ILogger<ContentTypeOnDemandRegistration>
    {
        private readonly object gate = new();
        private readonly List<(LogLevel Level, string Message)> entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (gate)
                    return entries.ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (gate)
                entries.Add((logLevel, message));
        }
    }
}