using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 Option C, end to end through the REAL NodeType compile
/// (<c>MeshNodeCompilationService.CompileAndGetConfigurations</c>): under
/// <c>Access:InMeshImpersonation:Mode=Enforce</c> a NodeType whose source references an
/// impersonation API is refused AT COMPILE, unless its path is on the trust list. The source uses
/// the two shapes the runtime guard cannot see — a call in tail position and a method group handed
/// to Rx — so this is the case the compile-time check exists for.
///
/// <para>Negative control: <see cref="InMeshImpersonationCompileCheckTrustedTest"/> compiles the
/// SAME source at a trusted path under Enforce and must succeed, so a refusal here is the guard's and
/// not a broken source.</para>
/// </summary>
public abstract class InMeshImpersonationCompileCheckTestBase(ITestOutputHelper output, string mode, params string[] trusted)
    : MonolithMeshTestBase(output)
{
    /// <summary>Escalating source: a tail-call impersonation and a method group handed to Rx.</summary>
    protected const string EscalatingSource = """
        using System;
        using System.Reactive.Linq;
        using MeshWeaver.Messaging;

        public record ImpersonationProbe(string Id);

        public static class Escalate
        {
            public static IDisposable Now(AccessService access) => access.ImpersonateAsSystem();

            public static IObservable<int> Later(AccessService access) =>
                Observable.Using(access.ImpersonateAsSystem, _ => Observable.Return(1));
        }
        """;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).ConfigureServices(services =>
        {
            // LAYER the mode onto the host's configuration, never replace it.
            var configured = services.LastOrDefault(d => d.ServiceType == typeof(IConfiguration));
            if (configured is not null)
                services.Remove(configured);
            return services.AddSingleton<IConfiguration>(sp =>
            {
                var config = new ConfigurationBuilder();
                if (configured?.ImplementationFactory is { } factory)
                    config.AddConfiguration((IConfiguration)factory(sp));
                else if (configured?.ImplementationInstance is IConfiguration instance)
                    config.AddConfiguration(instance);
                var settings = new Dictionary<string, string?> { [Messaging.InMeshImpersonationGuard.ModeKey] = mode };
                for (var i = 0; i < trusted.Length; i++)
                    settings[$"{Messaging.InMeshImpersonationGuard.TrustedCodeKey}:{i}"] = trusted[i];
                return config.AddInMemoryCollection(settings).Build();
            });
        });

    protected IMeshNodeCompilationService Compiler =>
        Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>();

    protected static MeshNode TypeNode(string path) => MeshNode.FromPath(path) with
    {
        Name = "ImpersonationProbe",
        NodeType = MeshNode.NodeTypePath,
        State = MeshNodeState.Active,
        LastModified = DateTimeOffset.UtcNow,
        Content = new NodeTypeDefinition
        {
            Description = "a type whose source references an impersonation API",
            Configuration = "config => config.WithContentType<ImpersonationProbe>()",
        },
    };

    protected static IReadOnlyList<MeshNode> Sources(string path) =>
    [
        new MeshNode("Escalate", $"{path}/Source")
        {
            NodeType = "Code",
            Name = "Escalate",
            State = MeshNodeState.Active,
            LastModified = DateTimeOffset.UtcNow,
            Content = new CodeConfiguration { Language = "csharp", Code = EscalatingSource },
        },
    ];

    /// <summary>Compiles and returns the result — or the fault, rendered, when the compile threw.</summary>
    protected async Task<(NodeCompilationResult? Result, string Failure)> CompileAsync(string path, System.Threading.CancellationToken ct)
    {
        var settled = await Compiler.CompileAndGetConfigurations(TypeNode(path), Sources(path))
            .Take(1)
            .Select(r => (Result: r, Failure: ""))
            .Catch((Exception ex) => Observable.Return(((NodeCompilationResult?)null, ex.ToString())))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the compile must settle", cancellationToken: ct);
        return settled;
    }

    /// <summary>Everything the failed compile says about itself, in one string.</summary>
    protected static string Describe((NodeCompilationResult? Result, string Failure) settled) =>
        settled.Failure + " " + string.Join(" ", settled.Result?.Log?.Messages.Select(m => m.Message) ?? []) + " "
        + string.Join(" ", settled.Result?.Diagnostics?.Select(d => d.Message) ?? []);
}

/// <summary>Enforce, nothing trusted: the escalating NodeType is refused at compile.</summary>
public class InMeshImpersonationCompileCheckEnforcedTest(ITestOutputHelper output)
    : InMeshImpersonationCompileCheckTestBase(output, Messaging.InMeshImpersonationMode.Enforce, "Governance")
{
    [Fact(Timeout = 300_000)]
    public async Task AnUntrustedNodeType_ReferencingImpersonation_IsRefusedAtCompile()
    {
        var settled = await CompileAsync("rbuergi/ImpersonationProbe", TestContext.Current.CancellationToken);

        settled.Result?.AssemblyLocation.Should().BeNullOrEmpty("a refused compile publishes no assembly");
        Describe(settled).Should().Contain("may not act as the platform",
            "the refusal is the guard's, and it names what was refused");
        Describe(settled).Should().Contain("ImpersonateAsSystem");
    }
}

/// <summary>NEGATIVE CONTROL — Enforce, the SAME source at a trusted path compiles.</summary>
public class InMeshImpersonationCompileCheckTrustedTest(ITestOutputHelper output)
    : InMeshImpersonationCompileCheckTestBase(output, Messaging.InMeshImpersonationMode.Enforce, "Governance")
{
    [Fact(Timeout = 300_000)]
    public async Task ATrustedNodeType_ReferencingImpersonation_Compiles()
    {
        var settled = await CompileAsync("Governance/ImpersonationProbe", TestContext.Current.CancellationToken);

        settled.Failure.Should().BeEmpty();
        settled.Result?.AssemblyLocation.Should().NotBeNullOrEmpty(
            "the source is valid; only the trust list separates this from the refused case: " + Describe(settled));
    }
}

/// <summary>The default (LogOnly): the escalating NodeType still compiles — nothing changes until the flip.</summary>
public class InMeshImpersonationCompileCheckLogOnlyTest(ITestOutputHelper output)
    : InMeshImpersonationCompileCheckTestBase(output, Messaging.InMeshImpersonationMode.LogOnly)
{
    [Fact(Timeout = 300_000)]
    public async Task LogOnly_AnUntrustedNodeType_StillCompiles()
    {
        var settled = await CompileAsync("rbuergi/ImpersonationProbe", TestContext.Current.CancellationToken);

        settled.Failure.Should().BeEmpty();
        settled.Result?.AssemblyLocation.Should().NotBeNullOrEmpty("LogOnly refuses nothing: " + Describe(settled));
    }
}
