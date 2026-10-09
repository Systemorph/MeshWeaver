using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A kernel script that compiles against a module's PRIVATE dependency must also RUN against
/// it.</b>
///
/// <para>The shape: a module runs in its own collectible context (policy
/// <c>module-live-update-default</c>) and ships a dependency the image does not — the Mail module
/// and Microsoft.Graph 6.2.0. A script that uses a type from that dependency compiles, because the
/// shared metadata resolver finds the file next to the module DLL that references it. At run time
/// the script session bound only module ENTRY assemblies, so the dependency fell through to the
/// default context, which cannot see the module's directory:
/// <c>FileNotFoundException: Could not load file or assembly 'Microsoft.Graph, Version=6.2.0.0'</c>
/// from <c>ScriptSession.ExecuteAsync</c>.</para>
///
/// <para>The run goes through the real path — a Code cell, <c>ExecuteScriptRequest</c>, the
/// Activity hub's kernel — and the verdict is read off the run's <see cref="ActivityLog"/>. The
/// script also hands a value of the dependency's type to the MODULE's own code, so it passes only
/// when the session binds the dependency with the module's identity, not merely some copy of it.</para>
/// </summary>
public sealed class KernelScriptRunsAgainstAModulesPrivateDependencyTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string Module = "MeshWeaver.Test.PrivateDepModule";
    private const string Dependency = "MeshWeaver.Test.PrivateDep";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(90);

    private readonly string root = Path.Combine(Path.GetTempPath(), "module-private-dep-" + Guid.NewGuid().ToString("N"));
    // Written when the mesh is configured — which the base runs from ITS constructor, before this
    // class's constructor body runs.
    private string? modulePath;

    private string ModulePath => modulePath ??= WriteModuleWithPrivateDependency();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).InstallAssemblies(ModulePath);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* a mapped DLL on a context still collecting — the OS reclaims it */ }
        catch (UnauthorizedAccessException) { }
    }

    [Fact(Timeout = 180_000)]
    public async Task AScriptUsingAModulesPrivateDependency_Runs_AndSharesTheModulesIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        var contexts = Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();
        var generation = contexts.Current(Module);
        generation.Should().NotBeNull("the arrangement: the module runs in its own context, held by the registry");
        File.Exists(Path.Combine(generation!.Context.Directory, Dependency + ".dll")).Should().BeTrue(
            "the arrangement: the dependency ships beside the module, and only there");

        var path = await CreateCell($$"""
            var envelope = new {{Dependency}}.Envelope("hello");
            {{Module}}.Mailer.Send(envelope)
            """, ct);
        var log = await RunToTerminal(path, ct);

        var transcript = string.Join(" | ", log.Messages.Select(m => m.Message));
        log.Status.Should().Be(ActivityStatus.Succeeded,
            "a script that compiled against the module's private dependency must be able to LOAD it — "
            + $"compile-time and run-time resolution have to agree. Transcript: {transcript}");
        log.Messages.Should().Contain(m => m.Message.Contains("sent:hello@" + Module),
            "the module's own code received the script's value — one identity for the dependency's type. "
            + $"Transcript: {transcript}");
    }

    // ── helpers ──

    private async Task<string> CreateCell(string code, CancellationToken ct)
    {
        var id = $"cell{Guid.NewGuid():N}"[..12];
        var path = $"{TestPartition}/{id}";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access
            .RunAsSystem(() => mesh.CreateNode(MeshNode.FromPath(path) with
            {
                NodeType = CodeNodeType.NodeType,
                Name = id,
                State = MeshNodeState.Active,
                Content = new CodeConfiguration { Code = code, IsExecutable = true },
            }))
            .FirstAsync()
            .Timeout(Bound)
            .Await(ct);
        return path;
    }

    /// <summary>Runs the cell through <c>ExecuteScriptRequest</c> and waits for its Activity to reach a terminal status.</summary>
    private async Task<ActivityLog> RunToTerminal(string path, CancellationToken ct)
    {
        var options = Mesh.JsonSerializerOptions;
        var dispatch = await RequestHub
            .Observe<ExecuteScriptResponse>(new ExecuteScriptRequest(), o => o.WithTarget(new Address(path)))
            .FirstAsync()
            .Timeout(Bound)
            .Await(ct);
        dispatch.Message.Success.Should().BeTrue($"the run of '{path}' must be accepted: {dispatch.Message.Error}");

        var cell = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(node => node is not null)
            .Select(node => node.ContentAs<CodeConfiguration>(options))
            .Where(c => c is { LastActivityPath: { Length: > 0 } })
            .FirstAsync()
            .Timeout(Bound)
            .Await(ct);

        return (await Mesh.GetWorkspace().GetMeshNodeStream(cell!.LastActivityPath!)
            .Where(node => node is not null)
            .Select(node => node.ContentAs<ActivityLog>(options))
            .Where(l => l is { Status: ActivityStatus.Succeeded or ActivityStatus.Failed or ActivityStatus.Cancelled })
            .FirstAsync()
            .Timeout(Bound)
            .Await(ct))!;
    }

    /// <summary>
    /// Emits the dependency (an assembly version the image does not carry, like Microsoft.Graph
    /// 6.2.0.0) and the module that references it into ONE generation directory — the landed layout
    /// of a module with a private closure — and returns the module's entry DLL.
    /// </summary>
    private string WriteModuleWithPrivateDependency()
    {
        var dependencyBytes = Emit(Dependency, $$"""
            [assembly: System.Reflection.AssemblyVersion("6.2.0.0")]
            namespace {{Dependency}};
            public sealed class Envelope(string subject)
            {
                public string Subject => subject;
            }
            """);
        var moduleBytes = Emit(Module, $$"""
            [assembly: {{Module}}.Module]
            namespace {{Module}};
            public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute { }
            public static class Mailer
            {
                public static string Send({{Dependency}}.Envelope envelope) =>
                    "sent:" + envelope.Subject + "@" + typeof(Mailer).Assembly.GetName().Name;
            }
            """, MetadataReference.CreateFromImage(dependencyBytes));

        var directory = Path.Combine(root, "modules", $"{Module}@g1");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, Dependency + ".dll"), dependencyBytes);
        var entry = Path.Combine(directory, Module + ".dll");
        File.WriteAllBytes(entry, moduleBytes);
        return entry;
    }

    private static byte[] Emit(string assemblyName, string source, MetadataReference? extra = null)
    {
        var references = PlatformReferences.Platform();
        if (extra is not null)
            references = references.Add(extra);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return buffer.ToArray();
    }
}
