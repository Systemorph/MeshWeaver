using System.Runtime.Loader;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The fixtures the three tests below share: a base module, and a dependent whose mesh-hub
/// configuration calls into it — the shape of <c>MeshWeaver.Blazor.Chat.BlazorChatExtensions.AddChatTypes</c>
/// calling into <c>MeshWeaver.AI</c>.
/// </summary>
internal static class UnresolvedDependencyFixture
{
    public static byte[] Base(string name) => AModuleWhoseDependencyIsAbsentIsWithdrawnAtBootTest.Compile(name, $$"""
        namespace {{name}};
        public static class Chat
        {
            public static MeshWeaver.Messaging.MessageHubConfiguration AddChatTypes(
                MeshWeaver.Messaging.MessageHubConfiguration configuration) => configuration;
        }
        """, []);

    public static byte[] Dependent(string name, string baseName, string basePath) =>
        AModuleWhoseDependencyIsAbsentIsWithdrawnAtBootTest.Compile(name, $$"""
            [assembly: {{name}}.Module]
            namespace {{name}};
            public static class BlazorChat
            {
                public static MeshWeaver.Messaging.MessageHubConfiguration AddChatTypes(
                    MeshWeaver.Messaging.MessageHubConfiguration configuration) =>
                    {{baseName}}.Chat.AddChatTypes(configuration);
            }
            public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
            {
                public override System.Collections.Generic.IEnumerable<System.Func<
                    MeshWeaver.Messaging.MessageHubConfiguration, MeshWeaver.Messaging.MessageHubConfiguration>> HubConfigurations =>
                    [BlazorChat.AddChatTypes];
            }
            """, [MetadataReference.CreateFromFile(basePath)]);

    public static string Write(string root, string directoryName, string fileName, byte[] image)
    {
        var directory = Path.Combine(root, "modules", directoryName);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName + ".dll");
        File.WriteAllBytes(path, image);
        return path;
    }
}

/// <summary>
/// 🚨 <b>build.meshweaver.cloud, 4.0.13590: a dependency the boot batch never held, whose file sits
/// beside ANOTHER module.</b> MeshWeaver#8399/#8401 withdrew a dependent only when its dependency was a
/// REFUSED member of the batch, and the same <c>FileNotFoundException</c> for <c>MeshWeaver.AI</c> took
/// the pod down again from <c>BlazorChatExtensions.AddChatTypes</c>: the dependency was skipped before
/// the batch, so no refusal named it. The link probe still passed the dependent, because its surface is
/// the files of EVERY directory the batch loads from, and a copy of the dependency lay in one of them.
/// The dependent's own load context resolves the platform, the CURRENT modules and its own directory —
/// never another module's private files — so the reference could not bind.
///
/// <para><b>Negative control.</b> With the boot's closure pass deciding by batch membership (the
/// #8401 form), the mesh hub cannot be built: the dependent's hub configuration throws
/// <see cref="FileNotFoundException"/> for the base.</para>
/// </summary>
public sealed class AModuleWhoseDependencyIsOnlyBesideAnotherModuleIsWithdrawnAtBootTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Base = "MeshWeaver.Test.BesideBase";
    private const string Dependent = "MeshWeaver.Test.BesideDependent";
    private const string Carrier = "MeshWeaver.Test.BesideCarrier";

    private readonly string root = Path.Combine(Path.GetTempPath(), "boot-closure-beside-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // The base is NOT a batch member: its only copy sits in the carrier's generation directory.
        var carrierPath = UnresolvedDependencyFixture.Write(root, Carrier + "@g1", Carrier,
            AModuleWhoseDependencyIsAbsentIsWithdrawnAtBootTest.Compile(Carrier,
                "namespace MeshWeaver.Test.BesideCarrier; public static class Carried { }", []));
        var basePath = UnresolvedDependencyFixture.Write(root, Carrier + "@g1", Base, UnresolvedDependencyFixture.Base(Base));
        var dependentPath = UnresolvedDependencyFixture.Write(root, Dependent + "@g1", Dependent,
            UnresolvedDependencyFixture.Dependent(Dependent, Base, basePath));
        return base.ConfigureMesh(builder).InstallAssemblies(dependentPath, carrierPath);
    }

    [Fact]
    public void TheProcessBoots_AndTheDependentIsWithdrawnNamingTheUnresolvableModule()
    {
        var refused = Mesh.ServiceProvider.GetServices<IncompatibleModule>().ToArray();
        var contexts = Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();

        // Positive controls: the base was never a batch member, and the carrier is untouched.
        refused.Should().NotContain(m => m.Name == Base, "the base was never in the batch, so nothing refused it");
        contexts.Current(Carrier).Should().NotBeNull("the carrier references nothing unresolvable");

        var dependent = refused.Should().ContainSingle(m => m.Name == Dependent,
            "the dependent's own load context cannot bind the base, so it is withdrawn and named").Subject;
        dependent.MissingMember.Should().Be(Base);
        dependent.Error.Should().Contain(Base).And.Contain("not in this boot's module batch");
        dependent.RefusedBeforeLoad.Should().BeTrue("a dependency missing from the module set is not something a roll fixes");
        contexts.Current(Dependent).Should().BeNull("a withdrawn module is not held");
    }
}

/// <summary>
/// 🚨 <b>A dependency the boot batch never held, LOADED into another load context of the process.</b>
/// The link probe's surface carries every assembly loaded in ANY context
/// (<c>AppDomain.GetAssemblies</c>), so a copy some other context holds passes the dependent — and the
/// dependent's module context, which resolves through the default context, the registry's current
/// modules and its own directory, still cannot bind it.
///
/// <para><b>Negative control.</b> With the #8401 closure pass, the mesh hub cannot be built: the
/// dependent's hub configuration throws <see cref="FileNotFoundException"/> for the base.</para>
/// </summary>
public sealed class AModuleWhoseDependencyIsLoadedOnlyInAnotherContextIsWithdrawnAtBootTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Base = "MeshWeaver.Test.ForeignBase";
    private const string Dependent = "MeshWeaver.Test.ForeignDependent";

    private readonly string root = Path.Combine(Path.GetTempPath(), "boot-closure-foreign-" + Guid.NewGuid().ToString("N"));
    private AssemblyLoadContext? foreign;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var basePath = UnresolvedDependencyFixture.Write(root, "elsewhere", Base, UnresolvedDependencyFixture.Base(Base));
        // Held by a context that is neither the default nor a module's, as a compiled NodeType or a
        // tool's metadata load would be.
        foreign = new AssemblyLoadContext("foreign-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        foreign.LoadFromAssemblyPath(basePath);
        var dependentPath = UnresolvedDependencyFixture.Write(root, Dependent + "@g1", Dependent,
            UnresolvedDependencyFixture.Dependent(Dependent, Base, basePath));
        return base.ConfigureMesh(builder).InstallAssemblies(dependentPath);
    }

    [Fact]
    public void TheProcessBoots_AndTheDependentIsWithdrawnNamingTheUnresolvableModule()
    {
        // Positive control: the base really is loaded in this process, so the probe's surface carries it.
        AppDomain.CurrentDomain.GetAssemblies().Should().Contain(a => a.GetName().Name == Base,
            "the copy in the foreign context is what made the probe pass");

        var refused = Mesh.ServiceProvider.GetServices<IncompatibleModule>().ToArray();
        var dependent = refused.Should().ContainSingle(m => m.Name == Dependent,
            "the dependent's own load context cannot bind a copy another context holds").Subject;
        dependent.MissingMember.Should().Be(Base);
        Mesh.ServiceProvider.GetRequiredService<ModuleContexts>().Current(Dependent).Should().BeNull();
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        foreign?.Unload();
    }
}

/// <summary>
/// The closure pass withdraws only what the module's resolver cannot bind: a dependency whose copy
/// sits in the dependent's OWN generation directory is its private closure (step 3 of
/// <see cref="ModuleLoadContext"/>), resolves, and the dependent is kept. Guards against the fix
/// withdrawing a module that runs.
/// </summary>
public sealed class AModuleWhoseDependencyIsInItsOwnDirectoryIsKeptAtBootTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Base = "MeshWeaver.Test.OwnBase";
    private const string Dependent = "MeshWeaver.Test.OwnDependent";

    private readonly string root = Path.Combine(Path.GetTempPath(), "boot-closure-own-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var basePath = UnresolvedDependencyFixture.Write(root, Dependent + "@g1", Base, UnresolvedDependencyFixture.Base(Base));
        var dependentPath = UnresolvedDependencyFixture.Write(root, Dependent + "@g1", Dependent,
            UnresolvedDependencyFixture.Dependent(Dependent, Base, basePath));
        return base.ConfigureMesh(builder).InstallAssemblies(dependentPath);
    }

    [Fact]
    public void TheDependentIsKept_BecauseItsOwnDirectoryCarriesTheDependency()
    {
        Mesh.ServiceProvider.GetServices<IncompatibleModule>().Should().NotContain(m => m.Name == Dependent);
        Mesh.ServiceProvider.GetRequiredService<ModuleContexts>().Current(Dependent)
            .Should().NotBeNull("its private closure resolves the dependency, and its hub configuration ran");
    }
}
