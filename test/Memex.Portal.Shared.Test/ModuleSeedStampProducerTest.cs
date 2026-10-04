using System.Diagnostics;
using System.Text;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The image-seed stamp's PRODUCER and READER agree (MeshWeaver#6044).</b>
///
/// <para>Boot prefers the image's own copy of a module over a store copy that is not a strictly
/// newer release — and the only record of what the image's copy WAS is the
/// <c>modules/&lt;Name&gt;/module.seed.json</c> the closure lane writes at image build
/// (<c>memex/MeshModulesPublish.targets</c>, <c>WriteMeshModuleSeedStamps</c>). A producer that
/// writes a field the reader does not look for, finds no package, or never runs at all leaves the
/// rule silently INERT: every image boots exactly as before #6044 and nothing says so. So this runs
/// the REAL task out of process, over a node-repository layout of the shape MeshWeaver.Plugins has
/// (<c>src/&lt;Module&gt;/</c> beside <c>&lt;Package&gt;/index.json</c> +
/// <c>manifest.lock</c>), and reads the result back through the production reader
/// (<see cref="ImageModuleSeed.Read"/>).</para>
///
/// <para>The second half is the stale case: a module no package declares gets NO stamp, and one left
/// over from an earlier publish into the same directory (publish never cleans) is removed — a stale
/// stamp would decide with a version the image no longer ships.</para>
///
/// <para>🚨 Fails RED, never skips, when <c>dotnet</c> cannot run the task: "could not measure"
/// must never read as "measured and fine".</para>
/// </summary>
public class ModuleSeedStampProducerTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-seedstamp-" + Guid.NewGuid().ToString("N"));

    public ModuleSeedStampProducerTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TheClosureLaneStamp_IsReadBackByTheBootReader_AndAStaleOneIsRemoved()
    {
        // A node repository: the package folder declares the module; the module's project lives
        // under src/, exactly the Plugins layout (src/MeshWeaver.AI ↔ AI/index.json).
        var package = Directory.CreateDirectory(Path.Combine(root, "repo", "AcmeWidgets")).FullName;
        File.WriteAllText(Path.Combine(package, "index.json"),
            """{ "$type": "MeshNode", "id": "AcmeWidgets", "content": { "$type": "PluginContent", "module": "Acme.Widgets", "version": "1.16" } }""");
        File.WriteAllText(Path.Combine(package, "manifest.lock"),
            """{ "files": {}, "module": "AcmeWidgets", "moduleVersion": "0123456789abcdef", "schema": "mw-manifest/1", "version": "1.16.3" }""");
        var declaredProject = Path.Combine(root, "repo", "src", "Acme.Widgets", "Acme.Widgets.csproj");
        var undeclaredProject = Path.Combine(root, "repo", "src", "Acme.Orphan", "Acme.Orphan.csproj");
        foreach (var project in new[] { declaredProject, undeclaredProject })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            File.WriteAllText(project, "<Project />");
        }

        var appDir = Path.Combine(root, "app") + Path.DirectorySeparatorChar;
        // A stale stamp from an earlier publish into the same directory, for the module no package
        // declares any more — it must not survive to decide with a version this image does not ship.
        var staleDir = Directory.CreateDirectory(Path.Combine(appDir, "modules", "Acme.Orphan")).FullName;
        File.WriteAllText(Path.Combine(staleDir, ImageModuleSeed.FileName),
            """{ "module": "Acme.Orphan", "version": "9.9.9" }""");

        var probe = Path.Combine(root, "probe.proj");
        File.WriteAllText(probe, $"""
            <Project>
              <Import Project="{TargetsPath()}" />
              <ItemGroup>
                <_Probe Include="{declaredProject}" />
                <_Probe Include="{undeclaredProject}" />
              </ItemGroup>
              <Target Name="Probe">
                <WriteMeshModuleSeedStamps Modules="@(_Probe)" AppDir="{appDir}" />
              </Target>
            </Project>
            """);

        var output = RunMsBuild(probe, "Probe");

        var seed = ImageModuleSeed.Read(Path.Combine(appDir, "modules", "Acme.Widgets"), "Acme.Widgets");
        Assert.True(seed is not null,
            $"the closure lane wrote no stamp the boot reader accepts — the #6044 rule would be inert. msbuild said:\n{output}");
        Assert.Equal("1.16.3", seed!.Version);
        Assert.Equal("AcmeWidgets", seed.Package);
        Assert.Equal("0123456789abcdef", seed.ModuleVersion);

        Assert.False(File.Exists(Path.Combine(staleDir, ImageModuleSeed.FileName)),
            "a module no package declares must carry no stamp — a stale one decides with a version the image does not ship");
    }

    private static string TargetsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var targets = Path.Combine(dir!.FullName, "memex", "MeshModulesPublish.targets");
        Assert.True(File.Exists(targets), $"{targets} is missing — follow the closure lane if it moved.");
        return targets;
    }

    /// <summary>Runs one target out of process. Both pipes are drained concurrently through the
    /// event-based reader (the MsBuildPropertyProbe analysis: a sequential drain deadlocks), and a
    /// wedge is a named failure, never a hang.</summary>
    private static string RunMsBuild(string project, string target)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("msbuild");
        psi.ArgumentList.Add(project);
        psi.ArgumentList.Add($"-t:{target}");
        psi.ArgumentList.Add("-nologo");
        psi.ArgumentList.Add("-warnaserror");

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        var outBuffer = new StringBuilder();
        var errBuffer = new StringBuilder();
        process!.OutputDataReceived += (_, e) => { if (e.Data is not null) outBuffer.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) errBuffer.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // Compiling an inline task and running it is a few seconds cold; two minutes is a wedge.
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            process.WaitForExit();
            Assert.Fail($"`dotnet msbuild -t:{target}` did not finish within 120s — a wedged MSBuild. Do not raise the bound.\n{outBuffer}\n{errBuffer}");
        }
        process.WaitForExit();
        var output = outBuffer + "\n" + errBuffer;
        Assert.True(process.ExitCode == 0, $"`dotnet msbuild -t:{target}` exited {process.ExitCode}:\n{output}");
        return output;
    }
}
