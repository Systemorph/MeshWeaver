using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A module whose dependency module did not load is withdrawn at boot, and the process boots.</b>
/// On build.meshweaver.cloud, 2026-11-18, every 4.0.13559 pod died at boot with exit 139. The crash dump
/// held <c>FileNotFoundException: Could not load file or assembly 'MeshWeaver.AI, Version=4.0.0.0'</c>
/// thrown from <c>MeshWeaver.Blazor.Chat.BlazorChatExtensions.AddChatTypes</c>, inside the mesh hub's
/// configuration, inside <c>MapMeshWeaver</c>. MeshWeaver.Blazor.Chat passed the boot link probe because
/// the probe's surface carries every directory the batch loads from, MeshWeaver.AI's included, whether
/// or not MeshWeaver.AI then loads. It did not load, so the first call into it took the process down
/// before the pod could serve, reconcile its module set or report why.
///
/// <para>The batch here is that incident in miniature: a base module linked against a
/// <c>MeshWeaver.*</c> assembly this deployment does not carry (so the link probe refuses it, and it
/// has no previous generation and no image copy to fall back to), and a dependent module whose
/// mesh-hub configuration calls into the base, as Blazor.Chat's does into AI.</para>
///
/// <para><b>Negative control.</b> With the boot's closure pass
/// (<c>MeshBuilder.WithdrawDependentsOfAbsentModules</c>) removed, the mesh hub cannot be built: the
/// dependent's hub configuration throws <see cref="FileNotFoundException"/> for the base, as on build.</para>
/// </summary>
public sealed class AModuleWhoseDependencyIsAbsentIsWithdrawnAtBootTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Absent = "MeshWeaver.Test.ClosureNotDeployed";
    private const string Base = "MeshWeaver.Test.ClosureBase";
    private const string Dependent = "MeshWeaver.Test.ClosureDependent";

    private readonly string root = Path.Combine(Path.GetTempPath(), "boot-closure-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Never written to disk: the deployment does not carry it, so the base cannot link.
        var absent = MetadataReference.CreateFromImage(Compile(Absent, "namespace MeshWeaver.Test.ClosureNotDeployed; public sealed class Missing { }", []));
        var basePath = Write(Base, Compile(Base, """
            namespace MeshWeaver.Test.ClosureBase;
            public static class Chat
            {
                public static MeshWeaver.Test.ClosureNotDeployed.Missing? Unused;
                public static MeshWeaver.Messaging.MessageHubConfiguration AddChatTypes(
                    MeshWeaver.Messaging.MessageHubConfiguration configuration) => configuration;
            }
            """, [absent]));
        var dependentPath = Write(Dependent, Compile(Dependent, """
            [assembly: MeshWeaver.Test.ClosureDependent.Module]
            namespace MeshWeaver.Test.ClosureDependent;
            public static class BlazorChat
            {
                // The module's OWN method, whose BODY reaches the base — the shape of
                // BlazorChatExtensions.AddChatTypes: building the delegate touches nothing, the
                // first call does.
                public static MeshWeaver.Messaging.MessageHubConfiguration AddChatTypes(
                    MeshWeaver.Messaging.MessageHubConfiguration configuration) =>
                    MeshWeaver.Test.ClosureBase.Chat.AddChatTypes(configuration);
            }
            public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
            {
                public override System.Collections.Generic.IEnumerable<System.Func<
                    MeshWeaver.Messaging.MessageHubConfiguration, MeshWeaver.Messaging.MessageHubConfiguration>> HubConfigurations =>
                    [BlazorChat.AddChatTypes];
            }
            """, [MetadataReference.CreateFromFile(basePath)]));
        // The dependent first, as a sidecar can list it: the closure pass must not depend on order.
        return base.ConfigureMesh(builder).InstallAssemblies(dependentPath, basePath);
    }

    [Fact]
    public void TheProcessBoots_AndTheDependentIsWithdrawnNamingTheAbsentModule()
    {
        var refused = Mesh.ServiceProvider.GetServices<IncompatibleModule>().ToArray();

        // Positive control: the base really was refused, by the link probe, before it loaded.
        refused.Should().Contain(m => m.Name == Base && m.RefusedBeforeLoad,
            "the base links against an assembly this deployment does not carry");

        var dependent = refused.Should().ContainSingle(m => m.Name == Dependent,
            "a module whose dependency did not load cannot run, so it is withdrawn and named").Subject;
        dependent.MissingMember.Should().Be(Base);
        dependent.Error.Should().Contain(Base).And.Contain("did not load in this process");
        dependent.RefusedBeforeLoad.Should().BeTrue("the dependent inherits the case of the dependency it needs");

        Mesh.ServiceProvider.GetRequiredService<ModuleContexts>().Current(Dependent)
            .Should().BeNull("a withdrawn module is not held, so nothing resolves it as the current generation");
    }

    internal static byte[] Compile(string name, string source, IEnumerable<MetadataReference> extra)
    {
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)],
            PlatformReferences.Platform().AddRange(extra),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return buffer.ToArray();
    }

    private string Write(string name, byte[] image) => Write(root, name, "g1", image);

    internal static string Write(string root, string name, string generation, byte[] image)
    {
        var directory = Path.Combine(root, "modules", $"{name}@{generation}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".dll");
        File.WriteAllBytes(path, image);
        return path;
    }
}

/// <summary>
/// Review of #8400: the closure pass counts a module an EARLIER batch committed as present. When this
/// batch's newer generation of the base is refused, the registry keeps the earlier one current, and a
/// dependent resolves against it, so the dependent must not be withdrawn. The earlier generation here is
/// the base without the undeployed reference; the later one is the base that cannot link.
/// </summary>
public sealed class AModuleWhoseDependencyIsHeldFromAnEarlierBatchIsKeptTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Absent = "MeshWeaver.Test.ClosureNotDeployed";
    private const string Base = "MeshWeaver.Test.ClosureHeldBase";
    private const string Dependent = "MeshWeaver.Test.ClosureHeldDependent";

    private readonly string root = Path.Combine(Path.GetTempPath(), "boot-closure-held-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        const string Body = """
            namespace MeshWeaver.Test.ClosureHeldBase;
            public static class Chat
            {
                {{FIELD}}
                public static MeshWeaver.Messaging.MessageHubConfiguration AddChatTypes(
                    MeshWeaver.Messaging.MessageHubConfiguration configuration) => configuration;
            }
            """;
        var absent = MetadataReference.CreateFromImage(AModuleWhoseDependencyIsAbsentIsWithdrawnAtBootTest.Compile(
            Absent, "namespace MeshWeaver.Test.ClosureNotDeployed; public sealed class Missing { }", []));
        var healthy = AModuleWhoseDependencyIsAbsentIsWithdrawnAtBootTest.Write(root, Base, "g1",
            AModuleWhoseDependencyIsAbsentIsWithdrawnAtBootTest.Compile(Base, Body.Replace("{{FIELD}}", ""), []));
        var refused = AModuleWhoseDependencyIsAbsentIsWithdrawnAtBootTest.Write(root, Base, "g2",
            AModuleWhoseDependencyIsAbsentIsWithdrawnAtBootTest.Compile(Base,
                Body.Replace("{{FIELD}}", "public static MeshWeaver.Test.ClosureNotDeployed.Missing? Unused;"), [absent]));
        var dependent = AModuleWhoseDependencyIsAbsentIsWithdrawnAtBootTest.Write(root, Dependent, "g1",
            AModuleWhoseDependencyIsAbsentIsWithdrawnAtBootTest.Compile(Dependent, """
                [assembly: MeshWeaver.Test.ClosureHeldDependent.Module]
                namespace MeshWeaver.Test.ClosureHeldDependent;
                public static class BlazorChat
                {
                    public static MeshWeaver.Messaging.MessageHubConfiguration AddChatTypes(
                        MeshWeaver.Messaging.MessageHubConfiguration configuration) =>
                        MeshWeaver.Test.ClosureHeldBase.Chat.AddChatTypes(configuration);
                }
                public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
                {
                    public override System.Collections.Generic.IEnumerable<System.Func<
                        MeshWeaver.Messaging.MessageHubConfiguration, MeshWeaver.Messaging.MessageHubConfiguration>> HubConfigurations =>
                        [BlazorChat.AddChatTypes];
                }
                """, [MetadataReference.CreateFromFile(healthy)]));
        return base.ConfigureMesh(builder).InstallAssemblies(healthy).InstallAssemblies(dependent, refused);
    }

    [Fact]
    public void TheDependentKeepsRunning_AgainstTheGenerationTheEarlierBatchCommitted()
    {
        var refused = Mesh.ServiceProvider.GetServices<IncompatibleModule>().ToArray();
        var contexts = Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();

        // Positive control: the later generation of the base really was refused in the later batch.
        refused.Should().Contain(m => m.Name == Base, "the later base links against an assembly this deployment does not carry");
        contexts.Current(Base).Should().NotBeNull("the earlier batch's generation of the base stays current");

        refused.Should().NotContain(m => m.Name == Dependent,
            "its dependency is held from the earlier batch, so the reference resolves");
        contexts.Current(Dependent).Should().NotBeNull("the dependent is loaded and held");
    }
}
