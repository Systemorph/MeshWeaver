using System;
using System.IO;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the modules-folder machinery of design #1644, step 1: the fingerprint the compile
/// write-back stamps, the stamp semantics (preserve, never erase), and the module-path probing
/// the <c>Modules:Assemblies</c> loader resolves through.
/// </summary>
public class InstalledModulesTest
{
    [Fact]
    public void Fingerprint_IsEmptyForNoModules_AndStableOrderIndependent()
    {
        Assert.Equal(string.Empty, new InstalledModulesFingerprint([]).Hash);

        var a = new InstalledModuleAssembly(typeof(InstalledModulesTest).Assembly);
        var b = new InstalledModuleAssembly(typeof(MeshBuilder).Assembly);

        var forward = new InstalledModulesFingerprint([a, b]).Hash;
        var reversed = new InstalledModulesFingerprint([b, a]).Hash;
        Assert.NotEqual(string.Empty, forward);
        // Sorted MVIDs — registration order can never flap the fingerprint.
        Assert.Equal(forward, reversed);
        // Duplicate registrations (a module both referenced and listed) collapse.
        Assert.Equal(forward, new InstalledModulesFingerprint([a, b, a]).Hash);
        // A different module set is a different hash.
        Assert.NotEqual(forward, new InstalledModulesFingerprint([a]).Hash);
    }

    [Fact]
    public void ApplyCompileSuccess_StampsTheModulesHash_AndNeverErasesAStampedOne()
    {
        var def = new NodeTypeDefinition();
        var result = new NodeCompilationResult("some.dll", []);

        var stamped = NodeTypeCompilationHelpers.ApplyCompileSuccess(
            def, result, currentNodeVersion: 1, activityPath: null, releasePath: null,
            modulesHash: "abc123");
        Assert.Equal("abc123", stamped.CompiledModulesHash);

        // A caller without a resolvable fingerprint (null) must PRESERVE the recorded hash —
        // erasing it would forge "compiled without modules" onto a build that wasn't.
        var preserved = NodeTypeCompilationHelpers.ApplyCompileSuccess(
            stamped, result, currentNodeVersion: 2, activityPath: null, releasePath: null);
        Assert.Equal("abc123", preserved.CompiledModulesHash);
    }

    [Fact]
    public void ResolveModulePath_PrefersTheModulesFolder_AndFallsBackToBaseDirectory()
    {
        // Rooted entries pass through untouched.
        var rooted = Path.Combine(Path.GetTempPath(), "X.dll");
        Assert.Equal(rooted, MeshBuilder.ResolveModulePath(rooted));

        // No modules/<name>/ folder → the classic BaseDirectory-relative resolution.
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "NoSuchModule.dll"),
            MeshBuilder.ResolveModulePath("NoSuchModule.dll"));

        // A modules/<name>/<name>.dll beside the app WINS — the #1644 publish layout.
        var name = $"ProbeModule{Guid.NewGuid():N}";
        var folder = Path.Combine(AppContext.BaseDirectory, "modules", name);
        Directory.CreateDirectory(folder);
        var dll = Path.Combine(folder, name + ".dll");
        try
        {
            File.WriteAllBytes(dll, []);
            Assert.Equal(dll, MeshBuilder.ResolveModulePath(name + ".dll"));
        }
        finally
        {
            File.Delete(dll);
            Directory.Delete(folder);
        }
    }
}
