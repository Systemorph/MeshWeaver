using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>On-demand content-type registration of a NodeType path that a NON-declaration occupies
/// answers a named verdict — it does not log an Error conversion fault</b> (#6525, the #2993 /
/// #5264 family, <c>Doc/Architecture/DanglingNodeTypes</c> hole C).
///
/// <para>Measured on a portal pod: one <c>fail</c> line from
/// <c>MeshWeaver.Hosting.ContentTypeOnDemandRegistration</c> —
/// <c>As&lt;NodeTypeDefinition&gt; for Feedback: value is PluginContent …, not convertible</c> — for
/// the bare path <c>Feedback</c>, which holds a Store plugin root while the type lives at
/// <c>Feedback/Feedback</c>. A read asks for the type an instance NAMES, so the occupant is a normal
/// input; the attempt handed it to the NodeType enumeration's typing, whose logger reports an
/// unconvertible record at Error, naming neither the instance nor the reason.</para>
///
/// <para>The occupant here is a <c>Code</c> node with typed <see cref="CodeConfiguration"/> content
/// (untyped JSON would convert to a <see cref="NodeTypeDefinition"/> and prove nothing).
/// <b>SHOULD-FAIL-IF</b> the attempt types the occupant with its logger again: the Error line
/// returns and the outcome's detail no longer names the occupant.</para>
/// </summary>
public class AnOccupiedNodeTypePathIsNotAConversionFaultTest : MonolithMeshTestBase
{
    private readonly ErrorLines errors = new();
    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];
    private string OccupantPath => $"{TestPartition}/Occupied{suffix}";

    /// <summary>Captures Error lines from every logger the mesh creates.</summary>
    public AnOccupiedNodeTypePathIsNotAConversionFaultTest(ITestOutputHelper output) : base(output)
        => Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(errors));

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .ConfigureServices(services => services.AddSingleton<ContentTypeOnDemandRegistration>());

    [Fact(Timeout = 120_000)]
    public async Task RegisteringAPathANonDeclarationOccupies_NamesTheOccupant_AndLogsNoConversionFault()
    {
        var ct = TestContext.Current.CancellationToken;
        var occupant = new MeshNode($"Occupied{suffix}", TestPartition)
        {
            NodeType = "Code",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration { Language = "csharp", Code = "// not a NodeType" },
        };
        await Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateNode(occupant).Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the occupant must exist before a read names its path as a type", ct);

        // CONTROL: the occupant really is unconvertible, and the predicate the boundaries share calls
        // it provably not a declaration — otherwise this test would prove nothing about #6525.
        occupant.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions).Should().BeNull(
            "CONTROL — typed non-NodeType content does not convert");
        ContentTypeOnDemandRegistration.NotADeclaration(occupant, Mesh.JsonSerializerOptions).Should().NotBeNull(
            "CONTROL — the boundaries' one-sided predicate calls the occupant provably not a declaration");

        var outcome = await Mesh.ServiceProvider.GetRequiredService<ContentTypeOnDemandRegistration>()
            .EnsureRegistered(Mesh, OccupantPath)
            .Should().Within(TestTimeouts.Convergence).Emit("the attempt reaches one outcome", ct);

        errors.Lines.Where(l => l.Contains(OccupantPath, StringComparison.Ordinal)).Should().BeEmpty(
            "an occupied NodeType path is a normal input to a read's registration, never an Error conversion fault");
        outcome.Status.Should().Be(ContentTypeRegistrationStatus.NotBaked, outcome.Detail ?? "(no detail)");
        outcome.Detail.Should().Contain($"'{OccupantPath}' is a 'Code' node")
            .And.Contain("not a NodeType declaration");
    }

    private sealed class ErrorLines : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> lines = new();

        public string[] Lines => lines.ToArray();

        public ILogger CreateLogger(string categoryName) => new Capturing(this, categoryName);

        public void Dispose() { }

        private sealed class Capturing(ErrorLines owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error)
                    owner.lines.Enqueue($"{category}: {formatter(state, exception)}");
            }
        }
    }
}
