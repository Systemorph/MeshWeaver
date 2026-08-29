using MeshWeaver.Plugin.Packaging;
using System.Diagnostics;
using MeshWeaver.Plugin.Build;

// meshweaver-plugin-build <pluginDirectory> [--out <dir>] [--framework-version <v>]
//                         [--repo-root <dir> ...] [--no-build]
//
// Resolves a plugin's compilation units the way the portal does, emits one .csproj per unit, and
// (unless --no-build) builds them. Exit code 0 only when EVERY unit built: a partial plugin is
// worse than an unbuilt one, because a consumer resolving a mixed set gets a silent ABI mismatch.

// The module-pack verb (#1664): packs a built MODULE's closure into a bundle keyed to the
// framework MVID it was compiled against — invocable from any node repo's CI. Everything below
// this dispatch is the classic per-NodeType plugin build.
if (args.Length > 0 && args[0] == ModulePackCommand.Verb)
    return ModulePackCommand.Run(args.Skip(1).ToArray());

// The module-fetch verb: the CONSUMING half. A repo that depends on another fetches its RELEASED
// package here instead of cloning and recompiling it — the mechanism "build only what you own"
// needs (Doc/Architecture/ReleaseGates).
if (args.Length > 0 && args[0] == ModuleFetchCommand.Verb)
    return ModuleFetchCommand.Run(args.Skip(1).ToArray());

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        usage: meshweaver-plugin-build <pluginDirectory> [options]
               meshweaver-plugin-build module-pack <moduleOutputDir> [options]   (see module-pack --help)
               meshweaver-plugin-build module-fetch <package> [options]          (see module-fetch --help)

          --out <dir>                 where to write generated projects (default: ./obj/plugin-build)
          --framework-version <v>     MeshWeaver package version to compile against, or `latest`
                                      to resolve the newest from --source. Required — there is no
                                      default, because a stale one compiles silently.
          --repo-root <dir>           checkout root for resolving shared= includes (repeatable;
                                      defaults to the plugin's parent directory)
          --no-build                  emit projects only
          --pack <dir>                after a fully successful build, write a .nupkg here
          --package-version <v>       version for manifests that declare none (most do not)
          --source <uri|dir>          extra package source (repeatable). Point at the framework
                                      built from the current change to make this an ABI gate.
        """);
    return 0;
}

var pluginDirectory = Path.GetFullPath(args[0]);
if (!Directory.Exists(pluginDirectory))
{
    Console.Error.WriteLine($"error: plugin directory not found: {pluginDirectory}");
    return 2;
}

var outputDirectory = Path.Combine(Environment.CurrentDirectory, "obj", "plugin-build");
string? frameworkVersion = null;
var repoRoots = new List<string>();
var build = true;
string? packDirectory = null;
var restoreSources = new List<string>();
var packageVersion = "0.0.1";

for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out" when i + 1 < args.Length:
            outputDirectory = Path.GetFullPath(args[++i]);
            break;
        case "--framework-version" when i + 1 < args.Length:
            frameworkVersion = args[++i];
            break;
        case "--repo-root" when i + 1 < args.Length:
            repoRoots.Add(Path.GetFullPath(args[++i]));
            break;
        case "--no-build":
            build = false;
            break;
        case "--pack" when i + 1 < args.Length:
            packDirectory = Path.GetFullPath(args[++i]);
            break;
        case "--package-version" when i + 1 < args.Length:
            packageVersion = args[++i];
            break;
        case "--source" when i + 1 < args.Length:
            restoreSources.Add(args[++i]);
            break;
        default:
            Console.Error.WriteLine($"error: unrecognised argument '{args[i]}'");
            return 2;
    }
}

if (repoRoots.Count == 0)
    repoRoots.Add(Path.GetDirectoryName(pluginDirectory.TrimEnd(Path.DirectorySeparatorChar))!);

if (restoreSources.Count == 0)
    restoreSources.Add("https://api.nuget.org/v3/index.json");

// 🚨 No default version. A hard-coded one compiles every plugin against whatever was current when
// it was written — silently, because that framework is real and the build succeeds. Resolving
// `latest` records the FULL version including the .ci.<run> suffix, which is the part that says
// which build the API came from.
if (string.IsNullOrWhiteSpace(frameworkVersion))
{
    Console.Error.WriteLine(
        "error: --framework-version is required (an explicit version, or `latest` to resolve the "
        + "newest from --source)");
    return 2;
}

using var versionHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
try
{
    frameworkVersion = FrameworkVersionResolver.Resolve(frameworkVersion, restoreSources, versionHttp);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

Console.WriteLine($"framework: {frameworkVersion}");

// A malformed node file fails the build deliberately (silently skipping it would report success
// while testing less than claimed) — but it is an authoring error with an obvious fix, so it is
// reported as one line naming the file, not as an unhandled exception. A stack trace here says
// "the tool broke" when the truth is "this file has a typo".
System.Collections.Immutable.ImmutableArray<PluginUnit> units;
try
{
    units = PluginUnitResolver.Resolve(pluginDirectory, repoRoots);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
if (units.Length == 0)
{
    // Content-only plugins are legitimate and common (courses, agent/skill packs): six of the
    // twenty plugins in MeshWeaver.Plugins carry no C# at all. Nothing to compile is a SUCCESS.
    Console.WriteLine($"{Path.GetFileName(pluginDirectory)}: no compilation units (content-only plugin)");
    return 0;
}

Console.WriteLine($"{Path.GetFileName(pluginDirectory)}: {units.Length} compilation unit(s)");

var failures = 0;
foreach (var unit in units)
{
    var projectPath = ProjectEmitter.Emit(unit, frameworkVersion, outputDirectory, restoreSources);
    var shared = unit.Closure.Length - 1;
    Console.WriteLine($"  {unit.NodePath}  ({shared} shared include(s))");

    if (!build)
        continue;

    var process = Process.Start(new ProcessStartInfo("dotnet")
    {
        ArgumentList = { "build", projectPath, "-c", "Release", "--nologo" },
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    })!;
    // 🚨 BOTH reads must be started before either is awaited. Reading stdout to completion first
    // deadlocks as soon as the child fills stderr's pipe buffer: it blocks writing stderr, so it
    // never closes stdout, so this never returns. `dotnet build` emits plenty of both on a failing
    // unit — precisely the case this tool exists to report.
    var standardOutput = process.StandardOutput.ReadToEndAsync();
    var standardError = process.StandardError.ReadToEndAsync();
    Task.WaitAll(standardOutput, standardError);
    process.WaitForExit();
    var output = standardOutput.Result + standardError.Result;

    if (process.ExitCode == 0)
        continue;

    failures++;
    Console.Error.WriteLine($"  FAILED {unit.NodePath}");
    // 🚨 Match "error " generally, NOT "error CS". A unit fails for reasons the compiler never
    // reaches: NU1605 when a referenced package has no build at the framework version, MSB when
    // the SDK is wrong, a child that dies before emitting a diagnostic at all. Filtering to CS
    // discarded ALL of those and printed a bare "FAILED", which is how 33 of 33 code-bearing
    // packages once failed across a whole lane with not one diagnostic anywhere in the log.
    var diagnostics = output
        .Split('\n')
        .Where(l => l.Contains("error ", StringComparison.Ordinal))
        .Take(5)
        .ToArray();
    // Nothing matched: the build still failed, so it said SOMETHING. Print the tail rather than
    // nothing — an unrecognised failure is exactly the case where the raw output is all there is.
    if (diagnostics.Length == 0)
        diagnostics = output
            .Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .TakeLast(15)
            .ToArray();
    foreach (var line in diagnostics)
        Console.Error.WriteLine($"    {line.Trim()}");
    // A silent child is itself the finding — say so instead of leaving the reader to wonder
    // whether the output was empty or merely filtered away.
    if (diagnostics.Length == 0)
        Console.Error.WriteLine(
            $"    (dotnet build exited {process.ExitCode} without writing any output)");
    // A closure that resolved short is the likeliest cause, and the declared queries are the
    // only place that shows it — print them rather than making the next person go find the node.
    if (unit.DeclaredSources.Length > 0)
        Console.Error.WriteLine($"    declared sources: {string.Join(" | ", unit.DeclaredSources)}");
}

if (failures > 0)
{
    Console.Error.WriteLine($"{failures} of {units.Length} unit(s) failed");
    return 1;
}

Console.WriteLine($"all {units.Length} unit(s) {(build ? "built" : "emitted")}");

if (packDirectory is null)
    return 0;

// Reached only on a fully successful build — see the all-or-nothing note in the help text.
var manifest = PluginManifest.Read(pluginDirectory, packageVersion);
var packagePath = PluginPacker.Pack(
    pluginDirectory, manifest, units, outputDirectory, frameworkVersion, packDirectory);
Console.WriteLine($"packed {Path.GetFileName(packagePath)}");
return 0;
