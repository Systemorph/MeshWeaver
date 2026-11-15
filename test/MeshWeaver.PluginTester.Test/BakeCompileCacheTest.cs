using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// 🚨 <b>The bake's cross-run compile cache hits on unchanged inputs and misses on a surface
/// change</b> (<c>Doc/Architecture/NodeTypeCompileCache</c>). The Plugins gate recompiled every
/// NodeType on every run — 123 types, 5.6–5.9 minutes of a 15-minute job (MeshWeaver CI profile
/// 2026-11, R3) — because the four modules it composes are rebuilt by every run with a fresh MVID
/// and a commit stamp, so nothing keyed on their identity could ever be reused.
///
/// <para>The probe module here is rebuilt the same way: the private-only rebuild carries a
/// different commit stamp and a different method body, so its MVID differs, exactly as a CI
/// rebuild's does. The cache must reuse every compile across THAT rebuild, and must reuse none
/// across a PUBLIC change — the negative control without which a cache that always hits would
/// pass.</para>
/// </summary>
public class BakeCompileCacheTest(ITestOutputHelper output)
{
    private static readonly Regex CacheLine = new(
        @"bake: compile cache: (?<hits>\d+) of (?<total>\d+) cacheable NodeType compile\(s\) reused, (?<misses>\d+) compiled \((?<damaged>\d+) damaged entr\(ies\) ignored\), (?<stored>\d+) stored",
        RegexOptions.Compiled);

    [Fact(Timeout = 300_000)]
    public void SecondBake_ReusesEveryCompile_AcrossAPrivateRebuild_AndMissesOnAPublicChange()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var module = "CacheProbe" + Guid.NewGuid().ToString("N")[..8];
        var root = TempDirectory("mw-compile-cache");
        try
        {
            var repo = Path.Combine(root, "repo");
            WriteTree(repo, module);
            var cache = Path.Combine(root, "cache");

            var v1 = EmitModule(Path.Combine(root, "m1"), module, stamp: "g1111", privateBody: "return 1;", extraPublic: "");
            var first = BakeInItsOwnProcess(repo, Path.Combine(root, "b1"), v1, cache);
            Assert.Equal((0, 2, 2), (first.Hits, first.Misses, first.Stored));

            // ── Unchanged inputs: every compile is reused, and the bundles are the SAME bytes. ──
            var second = BakeInItsOwnProcess(repo, Path.Combine(root, "b2"), v1, cache);
            Assert.Equal((2, 0, 0), (second.Hits, second.Misses, second.Stored));
            Assert.Equal(DllsIn(Path.Combine(root, "b1")), DllsIn(Path.Combine(root, "b2")));

            // ── A CI-style rebuild: new commit stamp, new private body ⇒ new MVID, same surface. ──
            var v1Rebuilt = EmitModule(Path.Combine(root, "m1b"), module, stamp: "g2222", privateBody: "return 2;", extraPublic: "");
            Assert.NotEqual(MvidOf(v1), MvidOf(v1Rebuilt));
            Assert.Equal(AssemblySurface.Of(v1), AssemblySurface.Of(v1Rebuilt));
            var rebuilt = BakeInItsOwnProcess(repo, Path.Combine(root, "b3"), v1Rebuilt, cache);
            Assert.Equal((2, 0, 0), (rebuilt.Hits, rebuilt.Misses, rebuilt.Stored));
            Assert.Equal(DllsIn(Path.Combine(root, "b1")), DllsIn(Path.Combine(root, "b3")));
            // The reused build's edges name THIS run's build of the module — what a fresh compile writes.
            var edges = File.ReadAllText(Path.Combine(root, "b3", DependencyEdgesFile.FileName));
            Assert.Contains(MvidOf(v1Rebuilt), edges);
            Assert.DoesNotContain(MvidOf(v1), edges);

            // ── THE NEGATIVE CONTROL: a public surface change must miss for every dependent. ──
            var v2 = EmitModule(Path.Combine(root, "m2"), module, stamp: "g3333", privateBody: "return 1;",
                extraPublic: "public int Added { get; init; }");
            Assert.NotEqual(AssemblySurface.Of(v1), AssemblySurface.Of(v2));
            var changed = BakeInItsOwnProcess(repo, Path.Combine(root, "b4"), v2, cache);
            Assert.Equal((0, 2, 2), (changed.Hits, changed.Misses, changed.Stored));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact(Timeout = 300_000)]
    public void DamagedEntry_IsAMiss_AndTheTypeStillCompiles()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var module = "CacheProbe" + Guid.NewGuid().ToString("N")[..8];
        var root = TempDirectory("mw-compile-cache-damaged");
        try
        {
            var repo = Path.Combine(root, "repo");
            WriteTree(repo, module);
            var cache = Path.Combine(root, "cache");
            var v1 = EmitModule(Path.Combine(root, "m1"), module, stamp: "g1111", privateBody: "return 1;", extraPublic: "");
            Bake(repo, Path.Combine(root, "b1"), v1, cache);

            // Truncate every cached assembly: the decompression or the sha256 check must refuse them.
            foreach (var dll in Directory.EnumerateFiles(Path.Combine(cache, "entries"), "*.dll.gz", SearchOption.AllDirectories))
                File.WriteAllBytes(dll, [1, 2, 3]);

            var again = Bake(repo, Path.Combine(root, "b2"), v1, cache);
            Assert.Equal((0, 2, 2), (again.Hits, again.Misses, again.Damaged));
            // Recompiled, so NOT the cached bytes: a fresh emit records its own PDB path, which is
            // also why byte equality elsewhere in this class proves a REUSE and not a recompile.
            Assert.NotEqual(DllsIn(Path.Combine(root, "b1")), DllsIn(Path.Combine(root, "b2")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Surface_IgnoresProvenanceAndPrivates_AndSeesEveryPublicKindOfChange()
    {
        var root = TempDirectory("mw-surface");
        try
        {
            string Emit(string tag, string body) =>
                EmitRaw(Path.Combine(root, tag), "SurfaceProbe", body);
            const string baseline = """
                [assembly: System.Reflection.AssemblyMetadata("MeshWeaverFrameworkIdentity", "gAAAA")]
                namespace S;
                public class C { public int M(int x = 1) => x; private int P() => 1; public const int K = 1; }
                public struct V { private int hidden; }
                """;
            var a = AssemblySurface.Of(Emit("a", baseline));
            // Provenance stamp and a private member: same surface.
            Assert.Equal(a, AssemblySurface.Of(Emit("b", baseline.Replace("gAAAA", "gBBBB").Replace("private int P() => 1;", "private int P() => 2; private int Q() => 3;"))));
            // Each of these is a change a consumer's compile can see — and each must move it.
            Assert.NotEqual(a, AssemblySurface.Of(Emit("c", baseline.Replace("x = 1", "x = 2"))));          // default value
            Assert.NotEqual(a, AssemblySurface.Of(Emit("d", baseline.Replace("K = 1", "K = 2"))));          // inlined constant
            Assert.NotEqual(a, AssemblySurface.Of(Emit("e", baseline.Replace("int M(int x", "int M(int y").Replace("=> x;", "=> y;")))); // parameter name
            Assert.NotEqual(a, AssemblySurface.Of(Emit("f", baseline.Replace("private int hidden;", "private long hidden;")))); // struct field type
            Assert.NotEqual(a, AssemblySurface.Of(Emit("g", baseline.Replace("public class C", "[System.Obsolete] public class C")))); // attribute
        }
        finally
        {
            Cleanup(root);
        }
    }

    private sealed record BakeCounts(int Hits, int Misses, int Stored, int Damaged);

    private BakeCounts Bake(string repo, string outputDirectory, string modulePath, string cache)
    {
        var log = new StringWriter();
        var report = TreeBake.Run(new TreeBake.Options
        {
            RepoRoot = repo,
            OutputDirectory = outputDirectory,
            Output = log,
            ModuleAssemblyPaths = [modulePath],
            CompileCacheDirectory = cache,
        });
        output.WriteLine($"── bake → {Path.GetFileName(outputDirectory)} ──");
        output.WriteLine(log.ToString());
        Assert.Null(report.FatalError);
        Assert.Equal(2, report.Types.Length);
        Assert.All(report.Types, t => Assert.Null(t.Error));
        var match = CacheLine.Match(log.ToString());
        Assert.True(match.Success, "the bake printed no compile-cache line");
        return new BakeCounts(
            int.Parse(match.Groups["hits"].Value), int.Parse(match.Groups["misses"].Value),
            int.Parse(match.Groups["stored"].Value), int.Parse(match.Groups["damaged"].Value));
    }

    /// <summary>
    /// One bake per PROCESS, through the CLI — as CI runs it, and the only way to compose two builds
    /// of one module name: a process loads one build of a name and refuses a second.
    /// </summary>
    private BakeCounts BakeInItsOwnProcess(string repo, string outputDirectory, string modulePath, string cache)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "mw-plugin-test.exe" : "mw-plugin-test");
        Assert.True(File.Exists(exe), $"the tester binary is not beside the test assembly ('{exe}')");
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var argument in new[] { "compile", repo, "--output", outputDirectory, "--module", modulePath, "--compile-cache", cache })
            info.ArgumentList.Add(argument);
        var text = new StringBuilder();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => Append(text, e.Data);
        process.ErrorDataReceived += (_, e) => Append(text, e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"the bake had not exited after 3 minutes:\n{Read(text)}");
        }
        process.WaitForExit();
        var log = Read(text);
        output.WriteLine($"── bake (own process) → {Path.GetFileName(outputDirectory)} — exit {process.ExitCode} ──");
        output.WriteLine(log);
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("compile: 2/2 NodeType(s) compiled", log);
        var match = CacheLine.Match(log);
        Assert.True(match.Success, "the bake printed no compile-cache line");
        return new BakeCounts(
            int.Parse(match.Groups["hits"].Value), int.Parse(match.Groups["misses"].Value),
            int.Parse(match.Groups["stored"].Value), int.Parse(match.Groups["damaged"].Value));
    }

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is null)
            return;
        lock (builder)
            builder.AppendLine(line);
    }

    private static string Read(StringBuilder builder)
    {
        lock (builder)
            return builder.ToString();
    }

    private static void WriteTree(string repo, string module)
    {
        Write(repo, "Cached/index.json",
            """{"$type":"MeshNode","id":"Cached","namespace":"","path":"Cached","mainNode":"Cached","name":"Cached","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Compile-cache probe."}}""");
        foreach (var id in new[] { "Bound", "Plain" })
            Write(repo, $"Cached/{id}.json",
                $$$"""{"$type":"MeshNode","id":"{{{id}}}","namespace":"Cached","path":"Cached/{{{id}}}","mainNode":"Cached/{{{id}}}","name":"{{{id}}}","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"probe","configuration":"config => config.WithContentType<{{{id}}}>().AddDefaultLayoutAreas()","includeGlobalTypes":true}}""");
        Write(repo, "Cached/Bound/Source/Bound.cs",
            $$"""
            using {{module}};

            public record Bound
            {
                public Setting Setting { get; init; } = new();
            }
            """);
        Write(repo, "Cached/Plain/Source/Plain.cs",
            """
            public record Plain
            {
                public string Name { get; init; } = string.Empty;
            }
            """);
    }

    private static string EmitModule(string directory, string name, string stamp, string privateBody, string extraPublic) =>
        EmitRaw(directory, name,
            $$"""
            [assembly: System.Reflection.AssemblyMetadata("MeshWeaverFrameworkIdentity", "{{stamp}}")]
            namespace {{name}};

            public record Setting
            {
                public string Value { get; init; } = string.Empty;
                {{extraPublic}}
                private int Hidden() { {{privateBody}} }
            }
            """);

    private static string EmitRaw(string directory, string name, string source)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".dll");
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            ProbeReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        var emit = compilation.Emit(path);
        Assert.True(emit.Success, "probe did not compile: "
            + string.Join("; ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return path;
    }

    private static MetadataReference[] ProbeReferences() =>
    [
        .. ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "System.Private.CoreLib.dll" or "netstandard.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))),
    ];

    private static string MvidOf(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        return reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString("N");
    }

    /// <summary>Every assembly inside every bundle of a bake, by entry name → bytes (base64).</summary>
    private static SortedDictionary<string, string> DllsIn(string bakeDirectory)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var zip in Directory.EnumerateFiles(bakeDirectory, "*.zip"))
        {
            using var archive = ZipFile.OpenRead(zip);
            foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = entry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                result[Path.GetFileName(zip) + "!" + entry.FullName] = Convert.ToBase64String(memory.ToArray());
            }
        }
        Assert.NotEmpty(result);
        return result;
    }

    private static string TempDirectory(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));

    private static void Write(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void Cleanup(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}
