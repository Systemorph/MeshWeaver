#pragma warning disable CS1591

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MeshWeaver.Compiler;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>The pre-push gate must compile the program the mesh compiles</b>
/// (Systemorph/MeshWeaver#4711).
///
/// <para><c>.github/scripts/compile-check.py</c> is the gate every node repo and every agent runs
/// before pushing. It used to hand each of a NodeType's sources to MSBuild as its OWN
/// <c>&lt;Compile&gt;</c> item; the mesh does not —
/// <c>DynamicMeshNodeAttributeGenerator.GenerateAttributeSource</c> concatenates every source into
/// ONE compilation unit with the <c>using</c> directives hoisted and deduped. Under
/// <c>NullableContextOptions.Annotations</c> (what <c>EmitPipeline.CreateCompilationOptions</c>
/// sets, and what the gate mirrors) that is not cosmetic: nullable analysis runs only in text that
/// opted in with <c>#nullable enable</c>, and in one concatenated unit a directive in the FIRST
/// file is still in force in the LAST. Measured on MeshWeaver.Plugins/BusinessRules/Scope,
/// 2026-09-18, same tree both ways: the gate said <c>98 clean</c> while the bake reported
/// <c>CS8601 12×</c> and <c>CS8602 12×</c> on that one type — and the warning ratchet then filed
/// the pair under the NODETYPE's name, where nobody who owns it could reproduce it.</para>
///
/// <para><b>Why a parity test and not two careful implementations.</b> The same reasoning as
/// <c>ModulePlatformFloorScriptParityTest</c>: two call sites computing one fold differently either
/// never converge or never fire, and both are silent. The shaping is written once, in
/// <see cref="DynamicMeshNodeAttributeGenerator.ShapeAuthoredSource"/>, mirrored in the script, and
/// pinned here — for every fixture, the script's import block and stripped code must equal the
/// generator's, character for character.</para>
///
/// <para>🚨 The fixture asserts its own inputs: the script must exist, <c>python3</c> must run it,
/// and the fixture must actually EXERCISE the shapes that make the fold interesting (a directive
/// crossing a file boundary, a duplicate import, an alias, a <c>using static</c>, a <c>using
/// var</c> statement, a <c>using</c> line inside a block comment, and the format characters a
/// linguistic <c>StartsWith</c> matches straight through). A parity
/// test that silently skips — or that compares two renderings of nothing — is the skip-trapdoor
/// shape AGENTS.md bans: it would pass having checked nothing, exactly while the gate it pins was
/// unreachable.</para>
/// </summary>
public class ConcatenatedUnitParityTest : IDisposable
{
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "mw-unit-parity-" + Guid.NewGuid().ToString("N"));

    public ConcatenatedUnitParityTest() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_work))
                Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
        GC.SuppressFinalize(this);
    }

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }

    private static string ScriptPath
    {
        get
        {
            var script = Path.Combine(RepoRoot, ".github", "scripts", "compile-check.py");
            Assert.True(File.Exists(script),
                $"{script} is missing — this test would compare the generator against nothing while "
                + "the pre-push NodeType gate was unreachable. Follow the script if it moved.");
            return script;
        }
    }

    /// <summary>What the script shapes the named files into, as it would shape them for a compile.</summary>
    private static (string[] Imports, string Code) ScriptShaping(IEnumerable<string> files)
    {
        var psi = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(ScriptPath);
        psi.ArgumentList.Add("--emit-unit");
        foreach (var file in files)
            psi.ArgumentList.Add(file);

        // 🚨 Both pipes are drained by the EVENT callbacks, never by a blocking read, and the
        // timeout is taken on the WAIT. `ReadToEnd()` returns only at EOF, which a child produces
        // by exiting — so draining first and waiting second makes the wait unreachable on exactly
        // the hang it claims to bound: a wedged script blocks the read forever and the 60 s is
        // never consulted. (Copilot on #4727. It is the same shape as a gate whose verdict cannot
        // fail — a bound that only fires when it is not needed is not a bound.) Reading after the
        // wait is the other deadlock: a child that fills the pipe buffer blocks in `write` and
        // never exits.
        var stdoutBuffer = new StringBuilder();
        var stderrBuffer = new StringBuilder();
        psi.RedirectStandardInput = true;
        using var process = Process.Start(psi);
        Assert.NotNull(process);
        process!.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) lock (stdoutBuffer) stdoutBuffer.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) lock (stderrBuffer) stderrBuffer.AppendLine(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // The child reads nothing; closing stdin means a script that ever waited on input fails
        // fast instead of hanging until the bound.
        process.StandardInput.Close();

        if (!process.WaitForExit(milliseconds: 60_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the wait and the kill — nothing to stop.
            }
        }
        // The parameterless overload after a successful timed wait flushes the async readers, so
        // the buffers below are complete rather than whatever had arrived when the wait returned.
        process.WaitForExit();
        string stdout, stderr;
        lock (stdoutBuffer) stdout = stdoutBuffer.ToString();
        lock (stderrBuffer) stderr = stderrBuffer.ToString();
        Assert.True(process.HasExited,
            "compile-check.py --emit-unit did not exit within 60 s — it shapes text and compiles "
            + "nothing, so a wedge there is a defect, not slowness.");
        Assert.True(process.ExitCode == 0,
            $"compile-check.py --emit-unit exited {process.ExitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");

        using var document = JsonDocument.Parse(stdout);
        var imports = document.RootElement.GetProperty("imports")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        return (imports, document.RootElement.GetProperty("code").GetString()!);
    }

    /// <summary>What the MESH shapes the same files into — the generator itself, not a re-reading.</summary>
    private static (string[] Imports, string Code) GeneratorShaping(IEnumerable<string> files)
    {
        // The compile's own fold: read each source, combine in the given order, shape.
        // `File.ReadAllText` strips a UTF-8 byte-order mark, which is why the script reads
        // `utf-8-sig` — plain `utf-8` would carry a U+FEFF into the middle of the unit.
        var sources = files
            .Select(f => new CodeConfiguration { Code = File.ReadAllText(f), Language = "csharp" })
            .ToList();
        var combined = NodeCompileShaping.CombineSources(sources);
        var (imports, code) = DynamicMeshNodeAttributeGenerator.ShapeAuthoredSource(combined?.Code);
        return ([.. imports], code);
    }

    private string WriteSource(string name, string text, bool withBom = false)
    {
        var path = Path.Combine(_work, name);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: withBom));
        return path;
    }

    /// <summary>
    /// The fixture is deliberately nasty: it carries every shape whose handling differs between a
    /// per-file compile and a concatenated one. A file that is benign on its own is not evidence.
    /// </summary>
    private string[] NastyFixture() =>
    [
        WriteSource("A.cs", """
            #nullable enable
            using MeshWeaver.Data;
            using static MeshWeaver.Layout.Controls;
            using Snap = MeshWeaver.Mesh.MeshNode;

            /*
            using MeshWeaver.NeverImported;
            */
            public record A(string Name);
            """),
        WriteSource("B.cs", """
            using MeshWeaver.Data;
            using MeshWeaver.Layout;

            public class B
            {
                public string? Maybe;
                public int Length()
                {
                    using var handle = Maybe;
                    return handle.Length;
                }
            }
            """, withBom: true),
        // 🚨 Unicode FORMAT characters in front of a directive — the byte-order mark a generator
        // writing `Encoding.UTF8` after its own header produces (MeshWeaver.Plugins#2071: seven
        // committed scope proxies carried one), and its relatives. `Trim()` does NOT remove them —
        // they are not whitespace to `char.IsWhiteSpace` — but `ExtractUsingStatements` tests
        // `StartsWith("using ")` through the parameterless, CULTURE-SENSITIVE overload, and a
        // linguistic comparison gives a format character no collation weight. So the directive IS
        // hoisted, and it is emitted verbatim with the character still on it.
        //
        // This case exists because getting it wrong is silent and invents work: a reproduction
        // using an ORDINAL prefix test leaves the directive in the code, after other declarations,
        // and reports a CS1529 the portal would never produce — against content that is fine.
        WriteSource("C.cs", "// a generated header\n\uFEFFusing System.Text;\n"
                            + "\u200Busing System.Globalization;\n"
                            + "\u00ADusing System.Threading;\n"
                            + "public record C(Snap Node, StringBuilder Log);\n"),
    ];

    [Fact]
    public void TheScriptShapesTheUnitExactlyAsTheGeneratorDoes()
    {
        var files = NastyFixture();

        var (scriptImports, scriptCode) = ScriptShaping(files);
        var (meshImports, meshCode) = GeneratorShaping(files);

        // 🚨 The fixture must be doing work. Each of these would be true of a degenerate fixture
        // too, so they are asserted about the MESH's answer — the side that cannot be wrong by
        // agreeing with the other.
        meshImports.Should().Contain("using Snap = MeshWeaver.Mesh.MeshNode;",
            "an alias is hoisted by the mesh — the old gate dropped it, so the parity has to see one");
        meshImports.Should().Contain("using static MeshWeaver.Layout.Controls;",
            "a `using static` keeps its modifier");
        meshImports.Count(i => i == "using MeshWeaver.Data;").Should().Be(1,
            "the duplicate across A.cs and B.cs is deduped — that dedup is what keeps CS0105 off "
            + "content that correctly imports the same namespace twice");
        meshCode.Should().Contain("#nullable enable",
            "the directive survives into the unit — it is the whole reason the unit boundary matters");
        meshCode.Should().Contain("using var handle = Maybe;",
            "a `using var` STATEMENT stays where it is; hoisting it is CS8805/CS0103");
        meshCode.Should().Contain("using MeshWeaver.NeverImported;",
            "a `using` line inside a block comment is code, not a directive");
        meshImports.Should().Contain("\uFEFFusing System.Text;",
            "a format character does not stop the LINGUISTIC prefix test, so the directive is "
            + "hoisted — and `Trim()` does not remove the character, so it is emitted verbatim");
        meshImports.Should().Contain("\u200Busing System.Globalization;", "…same for U+200B");
        meshImports.Should().Contain("\u00ADusing System.Threading;", "…and for U+00AD");
        meshCode.Should().NotContain("using System.Text;",
            "the hoist is a MOVE, so nothing is left behind in the code — an ORDINAL prefix test "
            + "would leave it there and invent a CS1529 the portal never produces");
        meshCode.Split('\n').Count(l => l.StartsWith("\uFEFF", StringComparison.Ordinal))
            .Should().Be(0,
                "no U+FEFF reaches the code at all: each file's own leading BOM is stripped by the "
                + "read on both sides, and the mid-file ones went up with their directives");
        meshCode.IndexOf("#nullable enable", StringComparison.Ordinal)
            .Should().BeLessThan(meshCode.IndexOf("class B", StringComparison.Ordinal),
                "A.cs's directive precedes B.cs's code in one unit — which is exactly the analysis "
                + "B.cs never opted into and the old gate could not see");

        // …and the script must agree, character for character.
        scriptImports.Should().Equal(meshImports);
        scriptCode.Should().Be(meshCode);
    }

    [Fact]
    public void TheShapingsAgreeOnASingleSourceToo()
    {
        // CombineSources returns a lone file AS-IS rather than joining it; the script must not add
        // a separator either, or every single-source NodeType would differ by a blank line.
        var only = WriteSource(
            "Only.cs", "using MeshWeaver.Data;\r\npublic record Only(string Name);\r\n");

        var (scriptImports, scriptCode) = ScriptShaping([only]);
        var (meshImports, meshCode) = GeneratorShaping([only]);

        scriptImports.Should().Equal(meshImports);
        scriptCode.Should().Be(meshCode);
        meshCode.Should().NotContain("\r", "the extractor trims the carriage return off every line");
    }

    [Fact]
    public void TheShapingsAgreeOnASourcelessNodeType()
    {
        // A NodeType with no `.cs` at all still compiles — its `configuration` lambda is code. The
        // generator is handed null there; the script is handed no files. Both must produce the
        // standard import block and empty code, or a source-less type's lambda would be
        // type-checked in a different scope from the one it ships in.
        var (scriptImports, scriptCode) = ScriptShaping([]);
        var (meshImports, meshCode) = GeneratorShaping([]);

        scriptImports.Should().Equal(meshImports);
        scriptCode.Should().Be(meshCode);
        meshImports.Should().HaveCount(DynamicMeshNodeAttributeGenerator.StandardUsings.Length,
            "with no authored source the import block is exactly the standard set");
        meshCode.Should().BeEmpty();
    }
}
