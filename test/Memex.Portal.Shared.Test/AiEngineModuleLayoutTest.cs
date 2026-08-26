using System;
using System.IO;
using System.Reflection;
using MeshWeaver.Mesh;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The AI engine arrives as a MODULE, and the folder it arrives in is loadable (#2276).
///
/// <para>Memex.LocalMesh — the headless sidecar the React Native shells talk to — declares
/// <c>MeshWeaver.AI.dll</c> under <c>Modules:Assemblies</c>, which is what registers the
/// Thread/Agent/Skill node types its composer needs. Until this test the declaration was backed by
/// nothing an automated check could see:</para>
/// <list type="bullet">
///   <item>the engine first reached the sidecar as a <b>ProjectReference</b> riding the app closure
///     (8dd8eeecf) — the thin lane, chosen because this host had never imported the module lane at
///     all, so <c>modules/</c> was never created here;</item>
///   <item>and the compiler cannot speak to either arrangement, because the whole claim is that
///     NOTHING references the engine. A green build is silent about it by construction.</item>
/// </list>
/// <para>So this walks exactly the seam the sidecar walks at startup and nothing more — no model
/// call, no network: <c>ResolveModulePath</c> lands INSIDE <c>modules/MeshWeaver.AI/</c>, and the
/// engine's private deps rode the closure lane's prune. The failure it exists to catch is the one
/// that already shipped once: every chat send refused with "NodeType 'Thread' is not registered",
/// on a green build and a green rollout.</para>
///
/// <para>Mechanism and rationale are shared with <see cref="StorageModuleLayoutTest"/>; this is a
/// second SUBJECT on the same detector (<see cref="MeshBuilder.ResolveModulePath"/> and the real
/// lane in memex/MeshModulesPublish.targets), never a second copy of it. When the engine leaves for
/// MeshWeaver.Plugins, this file goes with the closure-lane row that stages it.</para>
/// </summary>
public class AiEngineModuleLayoutTest
{
    private const string ModuleName = "MeshWeaver.AI";

    /// <summary>
    /// The engine resolves from <c>modules/&lt;Name&gt;/</c> — NOT from the app-folder fallback,
    /// which is where a re-added ProjectReference would put it and would make the module
    /// declaration decorative again.
    /// </summary>
    [Fact]
    public void ClosureLane_LaysTheEngineOut_WhereResolveModulePathLooks()
    {
        var expectedFolder = Path.Combine(AppContext.BaseDirectory, "modules", ModuleName);
        var resolved = MeshBuilder.ResolveModulePath(ModuleName + ".dll");

        Assert.True(
            File.Exists(resolved),
            $"{ModuleName}: the closure lane laid out nothing loadable — ResolveModulePath returned "
            + $"'{resolved}', which does not exist. A host that imports no module lane creates no "
            + "modules/ folder at all (memex/MeshModulesPublish.targets must be imported by the host).");

        // WHERE it resolved is the assertion: ResolveModulePath falls back to the app folder when
        // modules/<Name>/<Name>.dll is absent, so "a file exists" would pass on the double-ship.
        Assert.Equal(Path.Combine(expectedFolder, ModuleName + ".dll"), resolved);
    }

    /// <summary>
    /// The engine's PRIVATE deps rode the prune. Named rather than counted: their presence in the
    /// module folder is the single thing the prune can take away, and the sidecar references none
    /// of them, so their absence surfaces only as a TypeLoadException at the first chat send.
    /// </summary>
    [Theory]
    [InlineData("Microsoft.Extensions.AI.dll")]      // the IChatClient abstraction the engine binds
    [InlineData("Microsoft.Agents.AI.dll")]          // the agent runtime itself
    public void ClosureLane_KeepsTheEnginesPrivateDependencies(string dependencyFile)
    {
        var dependency = Path.Combine(AppContext.BaseDirectory, "modules", ModuleName, dependencyFile);

        Assert.True(
            File.Exists(dependency),
            $"{ModuleName}: '{dependencyFile}' is missing from modules/{ModuleName}/. Nothing else "
            + "ships it — no host references the engine — so the module would load and then fault "
            + "at its first use. Check the closure lane's prune in memex/MeshModulesPublish.targets.");
        Assert.NotNull(Assembly.LoadFrom(dependency));
    }
}
