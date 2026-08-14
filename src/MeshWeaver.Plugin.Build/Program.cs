using System.Diagnostics;
using MeshWeaver.Plugin.Build;

// meshweaver-plugin-build <pluginDirectory> [--out <dir>] [--framework-version <v>]
//                         [--repo-root <dir> ...] [--no-build]
//
// Resolves a plugin's compilation units the way the portal does, emits one .csproj per unit, and
// (unless --no-build) builds them. Exit code 0 only when EVERY unit built: a partial plugin is
// worse than an unbuilt one, because a consumer resolving a mixed set gets a silent ABI mismatch.

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        usage: meshweaver-plugin-build <pluginDirectory> [options]

          --out <dir>                 where to write generated projects (default: ./obj/plugin-build)
          --framework-version <v>     MeshWeaver package version to compile against (default: 3.0.0-rc2)
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
var frameworkVersion = "3.0.0-rc2";
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

var units = PluginUnitResolver.Resolve(pluginDirectory, repoRoots);
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
    var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
    process.WaitForExit();

    if (process.ExitCode == 0)
        continue;

    failures++;
    Console.Error.WriteLine($"  FAILED {unit.NodePath}");
    foreach (var line in output.Split('\n').Where(l => l.Contains("error CS", StringComparison.Ordinal)).Take(5))
        Console.Error.WriteLine($"    {line.Trim()}");
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

Console.WriteLine($"all {units.Length} unit(s) built");

if (packDirectory is null)
    return 0;

// Reached only on a fully successful build — see the all-or-nothing note in the help text.
var manifest = PluginManifest.Read(pluginDirectory, packageVersion);
var packagePath = PluginPacker.Pack(
    pluginDirectory, manifest, units, outputDirectory, frameworkVersion, packDirectory);
Console.WriteLine($"packed {Path.GetFileName(packagePath)}");
return 0;
