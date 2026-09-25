using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The API-break catalogue — what a platform change does to a plugin compiled against the
/// previous platform, caught BEFORE deploy</b> (policy <c>platform-backwards-compatibility</c>,
/// Doc/Architecture/PlatformCompatibilityLadder).
///
/// <para>A plugin compiled against platform P1 must run, unchanged, on P2 of the same major. Each
/// case below builds a small platform assembly P1 with Roslyn, compiles a plugin against it ONCE,
/// then mutates the platform in exactly one way (P2) and measures TWO things:</para>
/// <list type="number">
/// <item><description><b>The static verdict</b> — <see cref="ModulePlatformLink"/> with
/// <see cref="ModuleLinkOptions.WithMembers"/>, the same probe the CI compatibility gate runs over
/// the deployed plugin set against a candidate platform.</description></item>
/// <item><description><b>The ground truth</b> — the plugin's bytes are LOADED next to P2 in a
/// collectible load context and executed. A static verdict that disagreed with this column would be
/// either a false alarm (a hold on a plugin that runs) or a miss (a portal that throws), so every
/// case asserts both, and the static column may never be greener than the runtime one.</description></item>
/// </list>
///
/// <para><b>What makes these able to FAIL.</b> Real assemblies, the real probe, the real loader;
/// nothing mocked, no rule re-derived. The in-class negative control
/// <see cref="ARemovedMember_IsInvisibleToTheTypeLevelVerdict_WhichIsWhyTheMemberHalfExists"/>
/// measures the removal case with the member half OFF and requires Linkable — so neutralising the
/// member walk turns every member-shaped break red while that control stays green (demonstrated
/// when this suite was written: six of the break cases failed with the walk's one reporting line
/// removed).</para>
/// </summary>
public class ModulePlatformMemberLinkTest : IDisposable
{
    /// <summary>The stand-in platform assembly. It carries the platform prefix on purpose: member
    /// checking covers the platform's own <c>MeshWeaver.*</c> assemblies.</summary>
    private const string Api = "MeshWeaver.Test.LadderApi";

    /// <summary>The assembly a type MOVES to in the forwarder case.</summary>
    private const string ApiCore = "MeshWeaver.Test.LadderApi.Core";

    private const string Plugin = "MeshWeaver.Test.LadderPlugin";

    /// <summary>Platform P1 — what the plugin is compiled against.</summary>
    private const string V1 = """
        [assembly: System.Reflection.AssemblyVersion("3.0.0.0")]
        namespace MeshWeaver.Test.Ladder;
        public class Calculator
        {
            public static int Version = 1;
            public int Compute(int x) => x + 1;
            public static T Echo<T>(T value) => value;
        }
        public class Widget
        {
            public Widget(string name) { Name = name; }
            public string Name { get; }
            public string Tag { get; init; } = "";
        }
        public class Box<T> { public T Put(T value) => value; }
        public interface IShape { double Area(); }
        public class Square : IShape { public double Area() => 4; }
        public enum Kind { A, B }
        public static class Describer { public static string Describe(Kind kind) => kind.ToString(); }
        public interface IHook { string Name(); }
        public abstract class ViewBase { public abstract string Render(); }
        public class Panel { }
        public class Greeter { public string Greet() => "hello"; }
        """;

    /// <summary>The plugin: calls a method, a static generic method, a constructor, a getter, an
    /// init-only setter, a static field, a member of a generic instantiation, an interface method
    /// and an enum-typed parameter — and IMPLEMENTS a platform interface, OVERRIDES a platform
    /// abstract class and DERIVES from a platform class. The shapes a compiled plugin has.</summary>
    private const string PluginSource = """
        using MeshWeaver.Test.Ladder;
        public sealed class MyHook : IHook { public string Name() => "hook"; }
        public sealed class MyView : ViewBase { public override string Render() => "view"; }
        public sealed class MyPanel : Panel { }
        public static class LadderPlugin
        {
            /// <summary>Runs every shape once.</summary>
            public static string Run()
            {
                var total = new Calculator().Compute(Calculator.Version) + Calculator.Echo(2);
                var widget = new Widget("w") { Tag = "t" };
                var boxed = new Box<int>().Put(total);
                IShape shape = new Square();
                IHook hook = new MyHook();
                var panel = new MyPanel();
                return widget.Name + widget.Tag + boxed + shape.Area() + Describer.Describe(Kind.B)
                    + hook.Name() + new MyView().Render() + (panel is Panel) + new Greeter().Greet();
            }
        }
        """;

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-memberlink-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the per-test root.</summary>
    public ModulePlatformMemberLinkTest() => Directory.CreateDirectory(root);

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
        GC.SuppressFinalize(this);
    }

    // ═════════════════════════════════════════════════════════════════ BREAKS — must be CAUGHT

    /// <summary>
    /// 🚨 Every BREAKING platform change the catalogue covers: (P1 text, P2 text, what the verdict
    /// must name). Each is caught by the static probe AND genuinely fails at run time — the
    /// catalogue's two columns agree.
    /// </summary>
    public static TheoryData<string, string, string, string> Breaks => new()
    {
        // A removed public type — the type half.
        { "removed public type", "public class Box<T> { public T Put(T value) => value; }", "", "MeshWeaver.Test.Ladder.Box`1" },
        // A renamed / moved namespace — every type in it is a different type now.
        { "renamed namespace", "namespace MeshWeaver.Test.Ladder;", "namespace MeshWeaver.Test.LadderRenamed;", "MeshWeaver.Test.Ladder.Calculator" },
        // A removed member on a type that stays — MissingMethodException.
        { "removed member", "public int Compute(int x) => x + 1;", "", "Calculator::Compute" },
        // A changed parameter TYPE.
        { "changed parameter type", "public int Compute(int x) => x + 1;", "public int Compute(long x) => (int)x + 1;", "Calculator::Compute" },
        // A changed parameter COUNT — an OPTIONAL parameter appended. Every SOURCE caller still
        // compiles; every COMPILED caller binds the old signature and throws. The shape a reviewer
        // waves through.
        { "optional parameter appended", "public int Compute(int x) => x + 1;", "public int Compute(int x, int step = 1) => x + step;", "Calculator::Compute" },
        // A changed RETURN type.
        { "changed return type", "public int Compute(int x) => x + 1;", "public long Compute(int x) => x + 1;", "Calculator::Compute" },
        // static ↔ instance.
        { "static made instance", "public static T Echo<T>(T value) => value;", "public T Echo<T>(T value) => value;", "Calculator::Echo" },
        // A member made non-public — it still exists by name; MethodAccessException.
        { "member made internal", "public int Compute(int x) => x + 1;", "internal int Compute(int x) => x + 1;", "Calculator::Compute" },
        // A TYPE made non-public — it still exists by name; TypeAccessException.
        { "type made internal", "public class Greeter { public string Greet() => \"hello\"; }", "internal class Greeter { public string Greet() => \"hello\"; }", "Greeter" },
        // A field turned into a property — MissingFieldException.
        { "field made property", "public static int Version = 1;", "public static int Version { get; set; } = 1;", "Calculator::Version" },
        // init → set: the IsExternalInit modreq is part of the setter's signature.
        { "init setter made set", "public string Tag { get; init; } = \"\";", "public string Tag { get; set; } = \"\";", "Widget::set_Tag" },
        // A removed constructor overload.
        { "constructor re-signed", "public Widget(string name) { Name = name; }", "public Widget(string name, int size) { Name = name; }", "Widget::.ctor" },
        // A member of a GENERIC type, referenced through its instantiation, re-signed.
        { "generic member re-signed", "public T Put(T value) => value;", "public T Put(T value, bool replace = false) => value;", "Box`1::Put" },
        // enum → open string constants (policy open-vocabulary-string-constants) WITHOUT a
        // forwarder: the enum-typed signature a compiled caller names is gone.
        { "enum made string constants",
          "public enum Kind { A, B }\npublic static class Describer { public static string Describe(Kind kind) => kind.ToString(); }",
          "public static class Kind { public const string A = \"A\"; public const string B = \"B\"; }\npublic static class Describer { public static string Describe(string kind) => kind; }",
          "Describer::Describe" },
        // An interface member added WITHOUT a default implementation, on an interface a plugin
        // IMPLEMENTS — TypeLoadException when the plugin type loads (#3465's shape at run time).
        { "interface member added", "public interface IHook { string Name(); }", "public interface IHook { string Name(); string Describe(); }", "MyHook does not implement MeshWeaver.Test.Ladder.IHook::Describe" },
        // An abstract member added to a class a plugin derives from.
        { "abstract member added", "public abstract class ViewBase { public abstract string Render(); }", "public abstract class ViewBase { public abstract string Render(); public abstract string Title(); }", "MyView does not implement MeshWeaver.Test.Ladder.ViewBase::Title" },
        // A base class a plugin derives from made sealed.
        { "base class sealed", "public class Panel { }", "public sealed class Panel { }", "MyPanel derives from MeshWeaver.Test.Ladder.Panel" },
    };

    /// <summary>
    /// 🚨 THE catalogue's break half: the static probe names the break (so the platform pull request
    /// that introduced it goes red before anything deploys), and the plugin's bytes, loaded next to
    /// the mutated platform, genuinely fail — the static column is not a false alarm.
    /// </summary>
    [Theory]
    [MemberData(nameof(Breaks))]
    public void ABreakingPlatformChange_IsCaughtStatically_AndReallyFailsAtRunTime(
        string @case, string p1Text, string p2Text, string named)
    {
        var p2 = Mutate(p1Text, p2Text);

        var verdict = Check(Platform(p2));
        var runtime = RunAgainst(Platform(p2));

        Assert.False(verdict.MayLoad, $"[{@case}] the static probe must refuse: {verdict.Report()}");
        Assert.Equal(ModuleLinkState.Unlinkable, verdict.State);
        Assert.Contains(verdict.MissingTypes.Concat(verdict.MissingMembers),
            m => m.Contains(named, StringComparison.Ordinal));
        Assert.True(runtime is not null,
            $"[{@case}] the ground truth: the plugin's bytes must genuinely fail next to this platform, "
            + "or the static verdict above is a false alarm");
    }

    // ═════════════════════════════════════════════════════════════ COMPATIBLE — must be ALLOWED

    /// <summary>
    /// Every NON-breaking platform change the catalogue covers: the static probe lets it through
    /// and the plugin runs. A gate that refused these would be switched off within the week.
    /// </summary>
    public static TheoryData<string, string, string> Compatible => new()
    {
        // The plugin against the platform it was built on.
        { "unchanged", "public class Panel { }", "public class Panel { }" },
        // An added member and an added overload (the overload's SOURCE-side trap, CS0419, is its
        // own fact below).
        { "added member and overload", "public int Compute(int x) => x + 1;", "public int Compute(int x) => x + 1;\n    public long Compute(long x) => x + 2;\n    public int Twice(int x) => 2 * x;" },
        // An added type.
        { "added type", "public class Panel { }", "public class Panel { }\npublic class AddedInTheNextMinor { }" },
        // A member moved to a base class — the runtime's member resolution walks the hierarchy.
        { "member moved to a base class",
          "public class Calculator\n{\n    public static int Version = 1;\n    public int Compute(int x) => x + 1;",
          "public class CalculatorBase { public int Compute(int x) => x + 1; }\npublic class Calculator : CalculatorBase\n{\n    public static int Version = 1;" },
        // An interface member added WITH a default implementation.
        { "default interface member added", "public interface IHook { string Name(); }", "public interface IHook { string Name(); string Describe() => Name(); }" },
        // A virtual (non-abstract) member added to a class a plugin derives from.
        { "virtual member added", "public abstract class ViewBase { public abstract string Render(); }", "public abstract class ViewBase { public abstract string Render(); public virtual string Title() => \"\"; }" },
        // A rename that keeps an [Obsolete] FORWARDER under the old signature — the sanctioned way
        // to retire a member inside a major.
        { "renamed with an [Obsolete] forwarder", "public int Compute(int x) => x + 1;", "public int Calculate(int x) => x + 1;\n    [System.Obsolete(\"Use Calculate.\")] public int Compute(int x) => Calculate(x);" },
        // The minor version bump itself: the loader rolls a 3.0.0.0 reference FORWARD to 3.1.0.0.
        { "minor version bump", "[assembly: System.Reflection.AssemblyVersion(\"3.0.0.0\")]", "[assembly: System.Reflection.AssemblyVersion(\"3.1.0.0\")]" },
    };

    /// <summary>The catalogue's compatible half — Linkable with a non-zero member denominator, and
    /// the plugin genuinely runs next to the changed platform.</summary>
    [Theory]
    [MemberData(nameof(Compatible))]
    public void ACompatiblePlatformChange_IsAllowed_AndThePluginRuns(string @case, string p1Text, string p2Text)
    {
        var p2 = Mutate(p1Text, p2Text);

        var verdict = Check(Platform(p2));
        var runtime = RunAgainst(Platform(p2));

        Assert.True(verdict.MayLoad, $"[{@case}] {verdict.Report()}");
        Assert.Empty(verdict.MissingMembers);
        Assert.True(verdict.CheckedMemberReferences >= 9,
            $"[{@case}] every member shape the plugin uses must be resolved; checked {verdict.CheckedMemberReferences}");
        Assert.True(runtime is null, $"[{@case}] the plugin must run next to this platform: {runtime}");
    }

    /// <summary>
    /// The minor bump is drift the loader rolls FORWARD over (<c>MayBind</c>: running ≥ compiled) —
    /// reported as an advisory, never refused.
    /// </summary>
    [Fact]
    public void AMinorVersionBump_IsReportedAsForwardDrift_NeverRefused()
    {
        var verdict = Check(Platform(Mutate("\"3.0.0.0\"", "\"3.1.0.0\"")));

        Assert.True(verdict.MayLoad, verdict.Report());
        Assert.Contains(verdict.Advisories, a => a.Contains(Api, StringComparison.Ordinal)
                                                 && a.Contains("3.1.0.0", StringComparison.Ordinal));
    }

    /// <summary>
    /// A type MOVED to another platform assembly behind <c>[TypeForwardedTo]</c> is still found —
    /// the probe follows the forwarder to the assembly that defines it, exactly as the loader does.
    /// </summary>
    [Fact]
    public void ATypeMovedBehindAForwarder_StillLinks_AndRuns()
    {
        const string moved = "public class Widget\n{\n    public Widget(string name) { Name = name; }\n    public string Name { get; }\n    public string Tag { get; init; } = \"\";\n}";
        Assert.Contains(moved, V1, StringComparison.Ordinal);
        var coreSource = "[assembly: System.Reflection.AssemblyVersion(\"3.1.0.0\")]\nnamespace MeshWeaver.Test.Ladder;\n" + moved;
        var core = Emit(ApiCore, coreSource);
        var apiSource = Mutate(moved, "")
            .Replace("\"3.0.0.0\")]", "\"3.1.0.0\")]\n[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(MeshWeaver.Test.Ladder.Widget))]", StringComparison.Ordinal);
        var directory = NewDirectory("platform-forwarded");
        var corePath = Path.Combine(directory, ApiCore + ".dll");
        File.WriteAllBytes(corePath, core);
        var apiPath = Path.Combine(directory, Api + ".dll");
        File.WriteAllBytes(apiPath, Emit(Api, apiSource, MetadataReference.CreateFromImage(core)));

        var verdict = ModulePlatformLink.Check(PluginPath(), ModulePlatformSurface.OfFiles([apiPath, corePath]), ModuleLinkOptions.WithMembers);
        var runtime = RunAgainst(apiPath, corePath);

        Assert.True(verdict.MayLoad, verdict.Report());
        Assert.Empty(verdict.MissingMembers);
        Assert.Null(runtime);
    }

    /// <summary>
    /// 🚨 An added OVERLOAD is binary-compatible — the compiled plugin runs — but it is a SOURCE
    /// break for a dependent whose documentation names the member without a parameter list:
    /// <c>&lt;see cref="Calculator.Compute"/&gt;</c> becomes ambiguous (CS0419), which a
    /// <c>-warnaserror</c> build refuses. The static probe cannot see a source break and must not
    /// pretend to; it bites at the ladder's NEXT rung — the plugin's rebuild against the running
    /// platform — and this names it there. The fix is on the dependent's side: spell the cref with
    /// its parameter list.
    /// </summary>
    [Fact]
    public void AnAddedOverload_IsBinaryCompatible_ButTheCrefAmbiguityBitesAtThePluginsRebuild()
    {
        var p2 = Platform(Mutate("public int Compute(int x) => x + 1;",
            "public int Compute(int x) => x + 1;\n    public long Compute(long x) => x + 2;"));
        const string documented = """
            using MeshWeaver.Test.Ladder;
            /// <summary>Wraps <see cref="Calculator.Compute"/>.</summary>
            public static class Documented
            {
                /// <summary>Calls it.</summary>
                public static int Call() => new Calculator().Compute(1);
            }
            """;

        Assert.True(Check(p2).MayLoad, "binary: the compiled plugin still links");
        Assert.Contains(Diagnostics(documented, p2), d => d.Id == "CS0419");
        Assert.DoesNotContain(Diagnostics(documented.Replace("Calculator.Compute\"", "Calculator.Compute(int)\"", StringComparison.Ordinal), p2),
            d => d.Id == "CS0419");
    }

    /// <summary>
    /// 🚨 <b>THE BLIND SPOT, stated as a test so it can never be read as covered.</b> A behaviour
    /// change behind an UNCHANGED signature — a method that returns something else, a service a
    /// plugin resolves that is no longer registered, a default that moved — is invisible to any
    /// surface check by construction: the bytes link and run. Only EXECUTING the deployed plugin set
    /// against the candidate platform (the ladder's runtime rung) can see it.
    /// </summary>
    [Fact]
    public void ABehaviourChangeBehindAnUnchangedSignature_IsInvisibleToTheSurfaceCheck()
    {
        var p2 = Platform(Mutate("public string Greet() => \"hello\";", "public string Greet() => \"bye\";"));

        var verdict = Check(p2);
        var output = Invoke(p2);

        Assert.True(verdict.MayLoad, verdict.Report());
        Assert.EndsWith("bye", output, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════════ the probe's own rules

    /// <summary>
    /// 🚨 <b>THE NEGATIVE CONTROL.</b> The removal, measured type-only: Linkable. This is the hole the
    /// member half closes — and the proof the break cases are measuring the member walk. If the
    /// member half is deleted, they go red and this one does not change.
    /// </summary>
    [Fact]
    public void ARemovedMember_IsInvisibleToTheTypeLevelVerdict_WhichIsWhyTheMemberHalfExists()
    {
        var platform = Platform(Mutate("public int Compute(int x) => x + 1;", ""));

        var typeOnly = ModulePlatformLink.Check(PluginPath(), Surface(platform), ModuleLinkOptions.TypesOnly);

        Assert.True(typeOnly.MayLoad, typeOnly.Report());
        Assert.Equal(0, typeOnly.CheckedMemberReferences);
        Assert.Contains("member references NOT checked", typeOnly.Report(), StringComparison.Ordinal);
    }

    /// <summary>The removal names the member WITH its signature and assembly, and the report names
    /// the runtime exception and the remedy.</summary>
    [Fact]
    public void ARemovedMethod_IsNamedWithItsSignatureAndAssembly()
    {
        var verdict = Check(Platform(Mutate("public int Compute(int x) => x + 1;", "")));

        var missing = Assert.Single(verdict.MissingMembers);
        Assert.Contains("MeshWeaver.Test.Ladder.Calculator::Compute", missing, StringComparison.Ordinal);
        Assert.Contains("System.Int32", missing, StringComparison.Ordinal);
        Assert.Contains(Api, missing, StringComparison.Ordinal);
        Assert.Contains("MissingMethodException", verdict.Report(), StringComparison.Ordinal);
        Assert.Contains("[Obsolete] forwarder", verdict.Report(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A published surface DOCUMENT describes type names only. Asked for members against one, the
    /// probe says it could not measure — never a type-level pass wearing a member-level label.
    /// </summary>
    [Fact]
    public void MembersAgainstAPublishedDocument_AreIndeterminate_NeverASilentTypeLevelPass()
    {
        var document = ModulePlatformSurface.FromJson(Surface(Platform(V1)).ToJson("c003e001"));

        var verdict = ModulePlatformLink.Check(PluginPath(), document, ModuleLinkOptions.WithMembers);

        Assert.Equal(ModuleLinkState.Indeterminate, verdict.State);
        Assert.False(verdict.MayLoad);
    }

    /// <summary>
    /// The JUDGED scope: a core pull request builds the core platform only, so a plugin's
    /// references into an assembly the portal host ships from another repository are reported
    /// unchecked, not refused as "absent" — and inside the scope the refusal is unchanged.
    /// </summary>
    [Fact]
    public void AnAssemblyOutsideTheJudgedScope_IsReportedUnchecked_NeverRefused()
    {
        var surface = ModulePlatformSurface.OfFiles([]);

        var outOfScope = ModulePlatformLink.Check(PluginPath(), surface,
            ModuleLinkOptions.WithMembers with
            {
                JudgedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MeshWeaver.Mesh.Contract" },
            });
        var inScope = ModulePlatformLink.Check(PluginPath(), surface, ModuleLinkOptions.WithMembers);

        Assert.True(outOfScope.MayLoad, outOfScope.Report());
        Assert.Contains(Api, outOfScope.UncheckedAssemblies);
        Assert.Equal(ModuleLinkState.Unlinkable, inScope.State);
    }

    // ═══════════════════════════════════════════════════════════════════════════════ helpers

    private static string Mutate(string p1Text, string p2Text)
    {
        Assert.Contains(p1Text, V1, StringComparison.Ordinal);
        return V1.Replace(p1Text, p2Text, StringComparison.Ordinal);
    }

    private ModuleLinkVerdict Check(string platformPath) =>
        ModulePlatformLink.Check(PluginPath(), Surface(platformPath), ModuleLinkOptions.WithMembers);

    private static ModulePlatformSurface Surface(string platformPath) =>
        ModulePlatformSurface.OfFiles([platformPath]);

    private string? pluginPath;

    /// <summary>The plugin, compiled ONCE against P1 and written into its own directory — the bytes
    /// that must keep running on every later platform of the major.</summary>
    private string PluginPath()
    {
        if (pluginPath is not null)
            return pluginPath;
        var bytes = Emit(Plugin, PluginSource, MetadataReference.CreateFromImage(Emit(Api, V1)));
        pluginPath = Path.Combine(NewDirectory("plugin"), Plugin + ".dll");
        File.WriteAllBytes(pluginPath, bytes);
        return pluginPath;
    }

    /// <summary>Compiles a platform variant into its own directory and returns the DLL path.</summary>
    private string Platform(string source)
    {
        var path = Path.Combine(NewDirectory("platform"), Api + ".dll");
        File.WriteAllBytes(path, Emit(Api, source));
        return path;
    }

    private string NewDirectory(string prefix)
    {
        var directory = Path.Combine(root, prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// THE GROUND TRUTH: loads the plugin's P1-compiled bytes next to <paramref name="platformPaths"/>
    /// in a collectible load context — the platform assemblies resolve to THOSE files, whatever
    /// version the plugin asked for, which is the running-platform binding the ladder requires — and
    /// executes it. Null when it ran; the exception that stopped it otherwise.
    /// </summary>
    private Exception? RunAgainst(params string[] platformPaths)
    {
        try
        {
            Invoke(platformPaths);
            return null;
        }
        catch (Exception exception)
        {
            return exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
        }
    }

    private string Invoke(params string[] platformPaths)
    {
        var context = new PlatformContext(platformPaths);
        try
        {
            var plugin = context.LoadFromAssemblyPath(PluginPath());
            var run = plugin.GetType("LadderPlugin", throwOnError: true)!.GetMethod("Run")!;
            return (string)run.Invoke(null, null)!;
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>A collectible load context that binds each platform simple name to the given file
    /// and everything else to the default context.</summary>
    private sealed class PlatformContext(string[] platformPaths) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var match = platformPaths.FirstOrDefault(p =>
                string.Equals(Path.GetFileNameWithoutExtension(p), assemblyName.Name, StringComparison.OrdinalIgnoreCase));
            return match is null ? null : LoadFromAssemblyPath(match);
        }
    }

    private static IEnumerable<Diagnostic> Diagnostics(string source, string platformPath)
    {
        var compilation = CSharpCompilation.Create(
            "MeshWeaver.Test.LadderPluginRebuild",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose))],
            PlatformReferences.Platform().Add(MetadataReference.CreateFromFile(platformPath)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.GetDiagnostics();
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
        Assert.True(result.Success, string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return buffer.ToArray();
    }
}
