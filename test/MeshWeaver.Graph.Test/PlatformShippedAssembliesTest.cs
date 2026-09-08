#pragma warning disable CS1591

using MeshWeaver.Compiler;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The PLATFORM-SHIPPED WITNESS (#3732): what a platform host actually ships, measured off that
/// host's application directory. Three readings, because the image ships assemblies three ways —
/// and a witness that reads only the first answers "not shipped" for every seeded module, which is
/// how <c>MeshWeaver.Markdown.Collaboration</c> came to ride 14 of MeshWeaver.Plugins' 37 bundles
/// while the portal image seeds it too.
/// </summary>
public class PlatformShippedAssembliesTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-platform-witness-" + Guid.NewGuid().ToString("N"));

    private string App => Path.Combine(root, "app");

    public PlatformShippedAssembliesTest() => Directory.CreateDirectory(App);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            // The resolver-agreement guard seeds a probe under the TEST HOST's own base directory —
            // the one directory MeshBuilder.ResolveModulePath treats as "the app" — so it is removed
            // here rather than left behind for the next run's witness to read as a shipped module.
            // 🚨 The probe's own folder ONLY. An empty `modules/` beside the app is inert (the
            // witness enumerates its subdirectories and finds none, the resolver falls through to
            // the app closure), and deleting it would delete a layout a build may legitimately have
            // produced — the module-closure lane writes exactly that folder for a real host.
            var probe = Path.Combine(
                AppContext.BaseDirectory, PlatformShippedAssemblies.SeededModulesFolder, ProbeName);
            if (Directory.Exists(probe))
                Directory.Delete(probe, recursive: true);
        }
        catch
        {
            // Leaked temp dirs are the OS's to reap; cleanup must not mask a failure.
        }
    }

    private void AppAssembly(string name) =>
        File.WriteAllText(Path.Combine(App, name + ".dll"), name);

    private void SurfaceManifest(params string[] names) =>
        File.WriteAllLines(
            Path.Combine(App, "meshweaver-surface.manifest"),
            names.Select(n => $"{n}=0000000000000000000000000000000000000000000000000000000000000000"));

    private void SeededModule(string name)
    {
        var directory = Path.Combine(App, PlatformShippedAssemblies.SeededModulesFolder, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".dll"), name);
    }

    /// <summary>
    /// All three witnesses answer, and each says WHICH one did — the evidence a strip decision is
    /// printed with, so a dropped file names the path it rests on rather than an opinion.
    /// </summary>
    [Fact]
    public void TheThreeWaysAnImageShipsAnAssembly_AreAllRead_WithTheirEvidence()
    {
        AppAssembly("MeshWeaver.Blazor.Views");           // (1) the app closure
        SurfaceManifest("MeshWeaver.Graph", "MeshWeaver.Layout");  // (2) the host's own record
        SeededModule("MeshWeaver.Markdown.Collaboration"); // (3) MeshModuleClosure's lane

        var (shipped, problem) = PlatformShippedAssemblies.Read(App);

        Assert.Null(problem);
        Assert.Equal(
            ["MeshWeaver.Blazor.Views", "MeshWeaver.Graph", "MeshWeaver.Layout",
             "MeshWeaver.Markdown.Collaboration"],
            shipped!.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(PlatformShipping.ApplicationClosure, shipped["MeshWeaver.Blazor.Views"].How);
        Assert.Equal(PlatformShipping.SurfaceManifest, shipped["MeshWeaver.Layout"].How);
        Assert.Equal(PlatformShipping.SeededModule, shipped["MeshWeaver.Markdown.Collaboration"].How);
        Assert.EndsWith(
            Path.Combine("modules", "MeshWeaver.Markdown.Collaboration",
                "MeshWeaver.Markdown.Collaboration.dll"),
            shipped["MeshWeaver.Markdown.Collaboration"].Evidence, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 THE READING THAT WAS MISSING, isolated. A <c>MeshModuleClosure</c> row puts the assembly
    /// under <c>modules/&lt;Name&gt;/</c> and touches NEITHER the app root NOR the surface manifest —
    /// the portal hosts' csproj comments say exactly that — so a witness that read only <c>/app</c>
    /// answered "not shipped" for MeshWeaver.AI and MeshWeaver.Markdown.Collaboration, the two names
    /// that account for all 27 duplicate slots measured on MeshWeaver.Plugins main 2026-09-08.
    /// </summary>
    [Fact]
    public void ASeededModule_IsShipped_EvenThoughTheAppRootAndTheManifestBothOmitIt()
    {
        AppAssembly("MeshWeaver.Graph");
        SurfaceManifest("MeshWeaver.Graph");
        SeededModule("MeshWeaver.AI");

        var (shipped, problem) = PlatformShippedAssemblies.Read(App);

        Assert.Null(problem);
        Assert.False(File.Exists(Path.Combine(App, "MeshWeaver.AI.dll")),
            "the fixture must not put it in the app root, or this would pass for the wrong reason");
        Assert.True(shipped!.ContainsKey("MeshWeaver.AI"));
        Assert.Equal(PlatformShipping.SeededModule, shipped["MeshWeaver.AI"].How);
    }

    /// <summary>A seeded folder holds that module's OWN private closure; those files are on no
    /// other module's probing path, so only <c>&lt;Name&gt;/&lt;Name&gt;.dll</c> counts. Reading the
    /// whole folder would forbid a legitimate ride and cost the ReflectionTypeLoadException the
    /// closure rule exists to prevent.</summary>
    [Fact]
    public void ASeededModulesPrivateClosure_IsNotItselfShipped()
    {
        AppAssembly("MeshWeaver.Graph");
        SeededModule("MeshWeaver.AI");
        File.WriteAllText(
            Path.Combine(App, PlatformShippedAssemblies.SeededModulesFolder, "MeshWeaver.AI",
                "MeshWeaver.Maps.dll"),
            "a private ride of the seeded module, on nobody else's probing path");

        var (shipped, _) = PlatformShippedAssemblies.Read(App);

        Assert.True(shipped!.ContainsKey("MeshWeaver.AI"));
        Assert.False(shipped.ContainsKey("MeshWeaver.Maps"));
    }

    /// <summary>Third-party assemblies version independently and ride by design — judging them
    /// would hold every bundle in the fleet for a property nobody claimed.</summary>
    [Fact]
    public void OnlyMeshWeaverNamesAreJudged()
    {
        AppAssembly("MeshWeaver.Graph");
        File.WriteAllText(Path.Combine(App, "Azure.Identity.dll"), "third party");
        File.WriteAllText(Path.Combine(App, "MeshWeaverish.dll"), "not a platform name");

        var (shipped, _) = PlatformShippedAssemblies.Read(App);

        Assert.Equal(["MeshWeaver.Graph"], shipped!.Keys);
    }

    /// <summary>
    /// 🚨 THE NEGATIVE CONTROL, and the reason this returns a problem instead of an empty set.
    /// "This host ships nothing" is not a fact any real platform directory produces — a portal /app
    /// carries 200+ MeshWeaver.* assemblies and the tester image 88. Answering it would make every
    /// caller's strip a silent no-op that logs exactly like a clean measurement, which is the
    /// gate-that-cannot-fail shape.
    /// </summary>
    [Fact]
    public void ADirectoryThatIsNotAPlatformApp_IsRefused_NotReadAsShippingNothing()
    {
        File.WriteAllText(Path.Combine(App, "readme.txt"), "not an app directory");

        var (shipped, problem) = PlatformShippedAssemblies.Read(App);

        Assert.Null(shipped);
        Assert.Contains("not a platform application directory", problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingDirectory_IsRefused()
    {
        var (shipped, problem) = PlatformShippedAssemblies.Read(Path.Combine(root, "nope"));

        Assert.Null(shipped);
        Assert.Contains("does not exist", problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 <b>THE WITNESS AND THE LOADER PROBE THE SAME TWO PLACES, IN THE SAME ORDER — pinned, not
    /// assumed.</b> <c>MeshBuilder.ResolveModulePath</c> is the runtime's own answer to "where does
    /// this deployment's copy of module X live": <c>&lt;app&gt;/modules/&lt;name&gt;/&lt;name&gt;.dll</c>
    /// first, then <c>&lt;app&gt;/&lt;name&gt;.dll</c>. Those are readings (3) and (1) of the witness,
    /// which is what makes "the platform ships this" mean the same thing to the packer that strips a
    /// riding copy and to the loader that would have had to choose between two of them — and it is
    /// the very call #3735/#3748's image-baseline fallback resolves through.
    ///
    /// <para>Without this guard the agreement is a coincidence of two files written days apart. With
    /// it, a resolver that grows a third probe location — or reorders the two — fails HERE, naming
    /// the reading the witness is missing, instead of silently letting a bundle carry a copy of
    /// something the loader would have found on its own.</para>
    ///
    /// <para>Run against <see cref="AppContext.BaseDirectory"/>, because that is the one directory
    /// the resolver treats as "the app" — it reads it from <c>AppContext</c>, not from an argument.
    /// The probe name is unique to this test so a parallel class cannot collide with it, and the
    /// seeded folder is removed in <see cref="Dispose"/>.</para>
    /// </summary>
    [Fact]
    public void TheWitnessProbesTheSamePlacesTheRuntimeResolverDoes()
    {
        var app = AppContext.BaseDirectory;
        var entry = ProbeName + ".dll";

        // (1) the app closure — the resolver's LAST resort, and what it returns when nothing is
        //     seeded. The witness records exactly this path as ApplicationClosure evidence.
        Assert.Equal(
            Path.Combine(app, entry),
            MeshBuilder.ResolveModulePath(entry));

        // (3) the seeded-module lane — the resolver's FIRST choice once the file is there, and the
        //     path the witness records as SeededModule evidence.
        var seeded = Path.Combine(app, PlatformShippedAssemblies.SeededModulesFolder, ProbeName);
        Directory.CreateDirectory(seeded);
        File.WriteAllText(Path.Combine(seeded, entry), "probe");
        Assert.Equal(
            Path.Combine(seeded, entry),
            MeshBuilder.ResolveModulePath(entry));

        // And the witness reads that very file, at that very path, as the reason the name is shipped.
        var (shipped, problem) = PlatformShippedAssemblies.Read(app);
        Assert.Null(problem);
        Assert.True(shipped!.ContainsKey(ProbeName),
            "the witness must see what the resolver just resolved — otherwise a bundle would carry a "
            + "copy of an assembly the loader finds on its own");
        Assert.Equal(PlatformShipping.SeededModule, shipped[ProbeName].How);
        Assert.Equal(MeshBuilder.ResolveModulePath(entry), shipped[ProbeName].Evidence);
    }

    /// <summary>A name no real assembly carries, so the probe cannot collide with a parallel test
    /// class or with anything the test host genuinely ships.</summary>
    private const string ProbeName = "MeshWeaver.ResolverAgreementProbe";

    [Theory]
    [InlineData("MeshWeaver", true)]
    [InlineData("MeshWeaver.AI", true)]
    [InlineData("meshweaver.ai", true)]
    [InlineData("MeshWeaverish", false)]
    [InlineData("Azure.Identity", false)]
    [InlineData("", false)]
    public void ThePlatformNamePredicate_IsTheOneSpelling(string name, bool expected) =>
        Assert.Equal(expected, PlatformShippedAssemblies.IsPlatformAssemblyName(name));
}
