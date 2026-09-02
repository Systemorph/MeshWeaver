#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.PluginTester;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// 🚨 <b>The bake compiles against — and is addressed to — the PLATFORM HOST, not the process it
/// happens to run in</b> (#3022). The tester image's <c>/app</c> is a strict subset of the portal's
/// (88 vs 219 assemblies on 3.0.0-rc9.ci.7534; <c>MeshWeaver.Maps</c>, <c>.AI</c>, <c>.Indexing</c>,
/// the Blazor and hosting halves exist only in the portal), so a bake that took its reference set,
/// its identity and its dependency-record environment from its own process could neither see what
/// every portal compiles against nor say so: four NodeTypes went RED with <c>CS0234 'Maps' does
/// not exist in the namespace 'MeshWeaver'</c> on source nobody had changed, and a whole release
/// wave waited behind them.
///
/// <para>The platform host is SYNTHESISED here — this test process's own assemblies laid into a
/// directory beside a surface manifest — because the property under test is "the bake reads the
/// HOST's directory, not the process", and a synthesised host whose identity differs from the
/// process's is the only fixture that can tell the two apart. (The test process is manifest-less
/// and resolves the commit-stamp / MVID fallback; the host resolves an <c>s…</c> surface identity
/// from its manifest.) The host's assemblies ARE this process's, so the toolchain-equality
/// invariant holds for the happy path and is broken deliberately for the refusal.</para>
/// </summary>
public class BakeAgainstPlatformHostTest(ITestOutputHelper output)
{
    private const string WidgetIndexJson =
        """{"$type":"MeshNode","id":"Widget","namespace":"","path":"Widget","mainNode":"Widget","name":"Widget Plugin","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"A widget plugin."}}""";

    private const string ThingNodeTypeJson =
        """{"$type":"MeshNode","id":"Thing","namespace":"Widget","path":"Widget/Thing","mainNode":"Widget/Thing","name":"Thing","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"A thing.","configuration":"config => config.WithContentType<Thing>().AddDefaultLayoutAreas()","includeGlobalTypes":true}}""";

    private const string ThingSource =
        """
        public record Thing
        {
            public string Name { get; init; } = string.Empty;
        }
        """;

    // ── the gap fixtures (one package, three NodeTypes, one bake) ─────────────────────────────

    private const string GapIndexJson =
        """{"$type":"MeshNode","id":"Gap","namespace":"","path":"Gap","mainNode":"Gap","name":"Gap","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Three ways a reference can be missing."}}""";

    private static string GapType(string id, string type) =>
        $$$"""{"$type":"MeshNode","id":"{{{id}}}","namespace":"Gap","path":"Gap/{{{id}}}","mainNode":"Gap/{{{id}}}","name":"{{{id}}}","nodeType":"NodeType","state":"Active","content":{"$type":"NodeTypeDefinition","description":"{{{id}}}","configuration":"config => config.WithContentType<{{{type}}}>().AddDefaultLayoutAreas()","includeGlobalTypes":true}}""";

    /// <summary>Binds a namespace only a SHIPPED-BUT-NOT-COMPOSED module declares — the Maps shape:
    /// the root namespace exists in the reference set, the child does not.</summary>
    private const string ShippedBoundSource =
        """
        using MeshWeaver.ProbeModule;

        public record ShippedBound
        {
            public ProbeSetting Setting { get; init; } = new();
        }
        """;

    /// <summary>Binds a namespace nothing anywhere declares.</summary>
    private const string NowhereBoundSource =
        """
        using MeshWeaver.Nowhere;

        public record NowhereBound
        {
            public string Name { get; init; } = string.Empty;
        }
        """;

    /// <summary>A genuine content error: the namespace IS referenced, the type does not exist.</summary>
    private const string ContentBugSource =
        """
        using MeshWeaver.Layout;

        public record ContentBug
        {
            public NoSuchControl Control { get; init; } = null!;
        }
        """;

    private const string ProbeModuleSource =
        """
        namespace MeshWeaver.ProbeModule;

        public record ProbeSetting
        {
            public string Value { get; init; } = string.Empty;
        }
        """;

    [Fact(Timeout = 300_000)]
    public void TheBakeIsKeyedToTheHost_AndCompilesAgainstItsAssemblies()
    {
        var repo = TempDirectory("mw-host-repo");
        var host = TempDirectory("mw-host-app");
        var bake = TempDirectory("mw-host-bake");
        try
        {
            WriteWidget(repo);
            SynthesizeHost(host);
            var hostIdentity = FrameworkBuildIdentity.ResolveIdentityForDirectory(host).Identity;
            Assert.NotNull(hostIdentity);
            Assert.StartsWith("s", hostIdentity, StringComparison.Ordinal);
            // The discriminating fact: the HOST resolves an identity this PROCESS does not.
            Assert.NotEqual(PrebuiltAssemblySeeder.LiveFrameworkMvid, hostIdentity);

            var log = new StringWriter();
            var report = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo,
                OutputDirectory = bake,
                SourceSha = "deadbeef",
                Output = log,
                AppDirectory = host,
                SharedFrameworksRoot = SharedFrameworksRoot(),
            });
            output.WriteLine(log.ToString());

            Assert.Null(report.FatalError);
            Assert.NotEmpty(report.Types);
            Assert.All(report.Types, t => Assert.Null(t.Error));
            // Keyed to the HOST — in the report, in the sealed identity file, and said in the log.
            Assert.Equal(hostIdentity, report.FrameworkIdentity);
            Assert.Equal(hostIdentity, File.ReadAllText(Path.Combine(bake, TreeBake.FrameworkMvidFile)).Trim());
            Assert.Contains("reference set = platform host", log.ToString(), StringComparison.Ordinal);
            Assert.Contains(host, log.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(repo);
            Cleanup(host);
            Cleanup(bake);
        }
    }

    [Fact]
    public void AHostWithoutASurfaceManifest_IsRefused_NeverFallenBackOn()
    {
        var repo = TempDirectory("mw-host-nomanifest-repo");
        var host = TempDirectory("mw-host-nomanifest-app");
        var bake = TempDirectory("mw-host-nomanifest-bake");
        try
        {
            WriteWidget(repo);
            SynthesizeHost(host, writeManifest: false);

            var report = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo,
                OutputDirectory = bake,
                Output = new StringWriter(),
                AppDirectory = host,
                SharedFrameworksRoot = SharedFrameworksRoot(),
            });

            Assert.NotNull(report.FatalError);
            Assert.Contains("resolves no framework identity", report.FatalError, StringComparison.Ordinal);
            Assert.Contains(FrameworkBuildIdentity.SurfaceManifestFileName, report.FatalError, StringComparison.Ordinal);
            Assert.Empty(report.Bundles);
            // Nothing sealed under any identity — the fallback layer is exactly what must not be
            // published under.
            Assert.False(File.Exists(Path.Combine(bake, TreeBake.FrameworkMvidFile)));
        }
        finally
        {
            Cleanup(repo);
            Cleanup(host);
            Cleanup(bake);
        }
    }

    /// <summary>
    /// 🚨 The invariant that makes recording the host's identity honest: the TOOLCHAIN that ran must
    /// be the host's own bytes. The host here ships a <c>MeshWeaver.Compiler.dll</c> that is a
    /// different assembly under that name, so its MVID differs from the one this process executes.
    /// </summary>
    [Fact]
    public void AHostWhoseToolchainThisProcessDoesNotRun_IsRefusedNamingTheAssembly()
    {
        var repo = TempDirectory("mw-host-toolchain-repo");
        var host = TempDirectory("mw-host-toolchain-app");
        var bake = TempDirectory("mw-host-toolchain-bake");
        try
        {
            WriteWidget(repo);
            SynthesizeHost(host);
            var compiler = Path.Combine(host, "MeshWeaver.Compiler.dll");
            File.Delete(compiler);   // the symlink — replace with another assembly's bytes
            File.Copy(Path.Combine(AppContext.BaseDirectory, "MeshWeaver.NuGet.dll"), compiler);

            var report = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo,
                OutputDirectory = bake,
                Output = new StringWriter(),
                AppDirectory = host,
                SharedFrameworksRoot = SharedFrameworksRoot(),
            });

            Assert.NotNull(report.FatalError);
            Assert.Contains("toolchain", report.FatalError, StringComparison.Ordinal);
            Assert.Contains("MeshWeaver.Compiler mvid", report.FatalError, StringComparison.Ordinal);
            Assert.Empty(report.Bundles);
        }
        finally
        {
            Cleanup(repo);
            Cleanup(host);
            Cleanup(bake);
        }
    }

    [Fact]
    public void AppDirectoryWithoutSharedFrameworks_IsRefused()
    {
        var repo = TempDirectory("mw-host-noshared-repo");
        var host = TempDirectory("mw-host-noshared-app");
        try
        {
            WriteWidget(repo);
            SynthesizeHost(host);

            var report = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo,
                OutputDirectory = TempDirectory("mw-host-noshared-bake"),
                Output = new StringWriter(),
                AppDirectory = host,
            });

            Assert.NotNull(report.FatalError);
            Assert.Contains("SharedFrameworksRoot", report.FatalError, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(repo);
            Cleanup(host);
        }
    }

    /// <summary>
    /// 🚨 A reference-set gap is NAMED in the verdict. The three shapes in one bake: a namespace only
    /// a shipped-but-not-composed module declares (the Maps shape — named as
    /// <c>reference set lacks … (portal-shipped, not composed: …)</c>), a namespace nothing declares
    /// (named with its two possible causes), and a genuine content error on a referenced namespace
    /// (NO attribution — the compiler's own line is the whole truth there).
    /// </summary>
    [Fact(Timeout = 300_000)]
    public void AReferenceSetGap_IsNamedInTheVerdict_AndAContentErrorIsNot()
    {
        var repo = TempDirectory("mw-gap-repo");
        var host = TempDirectory("mw-gap-app");
        var bake = TempDirectory("mw-gap-bake");
        var withModule = TempDirectory("mw-gap-bake-composed");
        try
        {
            Write(repo, "Gap/index.json", GapIndexJson);
            Write(repo, "Gap/ShippedBound.json", GapType("ShippedBound", "ShippedBound"));
            Write(repo, "Gap/ShippedBound/Source/ShippedBound.cs", ShippedBoundSource);
            Write(repo, "Gap/NowhereBound.json", GapType("NowhereBound", "NowhereBound"));
            Write(repo, "Gap/NowhereBound/Source/NowhereBound.cs", NowhereBoundSource);
            Write(repo, "Gap/ContentBug.json", GapType("ContentBug", "ContentBug"));
            Write(repo, "Gap/ContentBug/Source/ContentBug.cs", ContentBugSource);
            SynthesizeHost(host);
            // The module the host SHIPS under modules/ and this bake does NOT compose.
            var modulePath = EmitProbeModule(Path.Combine(host, "modules", "MeshWeaver.ProbeModule"));

            var log = new StringWriter();
            var report = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo,
                OutputDirectory = bake,
                Output = log,
                AppDirectory = host,
                SharedFrameworksRoot = SharedFrameworksRoot(),
            });
            output.WriteLine(log.ToString());
            Assert.Null(report.FatalError);

            var shipped = report.Types.Single(t => t.NodePath == "Gap/ShippedBound");
            Assert.NotNull(shipped.Error);
            Assert.Contains("CS0234", shipped.Error, StringComparison.Ordinal);
            Assert.Contains(
                "reference set lacks MeshWeaver.ProbeModule (portal-shipped, not composed: "
                + Path.Combine("modules", "MeshWeaver.ProbeModule", "MeshWeaver.ProbeModule.dll") + ")",
                shipped.Error, StringComparison.Ordinal);
            Assert.Contains("--module", shipped.Error, StringComparison.Ordinal);

            var nowhere = report.Types.Single(t => t.NodePath == "Gap/NowhereBound");
            Assert.NotNull(nowhere.Error);
            Assert.Contains(
                "no assembly in the reference set declares namespace 'MeshWeaver.Nowhere'",
                nowhere.Error, StringComparison.Ordinal);
            Assert.Contains("registry-modules", nowhere.Error, StringComparison.Ordinal);

            var bug = report.Types.Single(t => t.NodePath == "Gap/ContentBug");
            Assert.NotNull(bug.Error);
            Assert.Contains("NoSuchControl", bug.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("reference set lacks", bug.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("no assembly in the reference set", bug.Error, StringComparison.Ordinal);

            // THE CONTROL: composing the shipped module makes the Maps-shaped type compile — the
            // attribution named the actual fix.
            var composed = TreeBake.Run(new TreeBake.Options
            {
                RepoRoot = repo,
                OutputDirectory = withModule,
                Output = new StringWriter(),
                AppDirectory = host,
                SharedFrameworksRoot = SharedFrameworksRoot(),
                ModuleAssemblyPaths = [modulePath],
            });
            Assert.Null(composed.FatalError);
            Assert.Null(composed.Types.Single(t => t.NodePath == "Gap/ShippedBound").Error);
        }
        finally
        {
            Cleanup(repo);
            Cleanup(host);
            Cleanup(bake);
            Cleanup(withModule);
        }
    }

    [Fact]
    public void TheGateRefusesToRunAsAHostItIsNot_AndAcceptsTheOneItIs()
    {
        var host = TempDirectory("mw-gatehost-app");
        var bare = TempDirectory("mw-gatehost-bare");
        try
        {
            SynthesizeHost(host);
            var hostIdentity = FrameworkBuildIdentity.ResolveIdentityForDirectory(host).Identity!;

            Assert.Null(GateHostCheck.Verify(host, hostIdentity));

            var refused = GateHostCheck.Verify(host, PrebuiltAssemblySeeder.LiveFrameworkMvid);
            Assert.NotNull(refused);
            Assert.Contains(hostIdentity, refused, StringComparison.Ordinal);
            Assert.Contains(PrebuiltAssemblySeeder.LiveFrameworkMvid, refused, StringComparison.Ordinal);
            Assert.Contains("RUN AS the platform host", refused, StringComparison.Ordinal);

            SynthesizeHost(bare, writeManifest: false);
            var noIdentity = GateHostCheck.Verify(bare, hostIdentity);
            Assert.NotNull(noIdentity);
            Assert.Contains("resolves no framework identity", noIdentity, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(host);
            Cleanup(bare);
        }
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A platform host made of THIS process's assemblies: every <c>*.dll</c> beside the test
    /// assembly linked into <paramref name="directory"/>, this host's own <c>.deps.json</c> as the
    /// one deps file (the reference set reads package versions and the MeshWeaver binding identity
    /// from it), and a surface manifest with one line per <c>MeshWeaver.*</c> assembly. The hash
    /// values are synthetic and stable (they only have to make the directory resolve SOME <c>s…</c>
    /// identity, distinct from the process's fallback one).
    /// </summary>
    private static void SynthesizeHost(string directory, bool writeManifest = true)
    {
        Directory.CreateDirectory(directory);
        var source = AppContext.BaseDirectory;
        var manifest = new StringBuilder();
        foreach (var dll in Directory.GetFiles(source, "*.dll").OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dll);
            var target = Path.Combine(directory, name);
            try
            {
                File.CreateSymbolicLink(target, dll);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                File.Copy(dll, target);
            }
            var simple = Path.GetFileNameWithoutExtension(name);
            if (simple.StartsWith("MeshWeaver.", StringComparison.Ordinal))
                manifest.Append(simple).Append('=')
                    .Append(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(simple)))).Append('\n');
        }
        // The ONE deps.json the reference set reads package versions and the MeshWeaver binding
        // identity from — synthetic and minimal: the test host's own lists its test assemblies
        // (1.0.0.0) beside the framework's (3.0.0.0), which ContainerReferenceSet rightly refuses
        // as two binding identities, while a real portal's records exactly one.
        File.WriteAllText(Path.Combine(directory, "Host.deps.json"),
            """
            {
              "runtimeTarget": { "name": "net10.0" },
              "targets": {
                "net10.0": {
                  "Host/1.0.0": {},
                  "Some.Package/1.0.0": { "runtime": { "lib/net10.0/Some.Package.dll": { "assemblyVersion": "1.0.0.0" } } },
                  "MeshWeaver.Compiler/3.0.0": { "runtime": { "MeshWeaver.Compiler.dll": { "assemblyVersion": "3.0.0.0" } } }
                }
              },
              "libraries": {
                "Host/1.0.0": { "type": "project" },
                "Some.Package/1.0.0": { "type": "package", "serviceable": true }
              }
            }
            """);
        if (writeManifest)
            File.WriteAllText(Path.Combine(directory, FrameworkBuildIdentity.SurfaceManifestFileName), manifest.ToString());
    }

    /// <summary>The running runtime's <c>&lt;dotnet root&gt;/shared</c> — the test host runs on
    /// installed shared frameworks, so this is a real one.</summary>
    private static string SharedFrameworksRoot()
    {
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
            .TrimEnd(Path.DirectorySeparatorChar, '/');
        var shared = Path.GetDirectoryName(Path.GetDirectoryName(runtime));
        Assert.True(shared is not null && Directory.Exists(shared),
            $"could not locate the shared-frameworks root from '{runtime}'");
        return shared!;
    }

    private static void WriteWidget(string repo)
    {
        Write(repo, "Widget/index.json", WidgetIndexJson);
        Write(repo, "Widget/Thing.json", ThingNodeTypeJson);
        Write(repo, "Widget/Thing/Source/Thing.cs", ThingSource);
    }

    private static string EmitProbeModule(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "MeshWeaver.ProbeModule.dll");
        var compilation = CSharpCompilation.Create(
            "MeshWeaver.ProbeModule",
            [CSharpSyntaxTree.ParseText(ProbeModuleSource)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                .. ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
                    .Split(Path.PathSeparator)
                    .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                    .Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "netstandard.dll")
                    .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var emit = compilation.Emit(path);
        Assert.True(emit.Success,
            "the probe module did not compile: "
            + string.Join("; ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return path;
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
        catch { /* best effort — a locked temp dir must not fail the test */ }
    }
}
