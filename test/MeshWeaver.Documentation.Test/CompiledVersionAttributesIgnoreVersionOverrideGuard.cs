using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Compiler;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b><c>-p:Version=</c> must not reach a COMPILED attribute</b> (#3022) — the invariant the
/// framework build identity, and therefore the whole prebuilt-NodeType delivery lane, rests on.
///
/// <para><b>What went wrong.</b> <c>Directory.Build.props</c> stated the rule in prose ("$(Version)
/// — the run-numbered string — feeds NuGet package versions and Docker image tags ONLY. It must NOT
/// reach any COMPILED attribute") and then broke it in the very next line: the PropertyGroup that
/// derives <c>InformationalVersion</c>, <c>AssemblyVersion</c> and <c>FileVersion</c> from
/// <c>$(PlatformVersion)</c> carried <c>Condition="'$(Version)' == ''"</c>. Supplying
/// <c>-p:Version=</c> made <c>$(Version)</c> a global property, skipped the whole group, and left
/// the three attributes to the SDK's defaults — which derive them from <c>$(Version)</c>, the
/// run-numbered string.</para>
///
/// <para><b>What it cost.</b> CD's <c>portal-image</c> leg passes <c>-p:Version=</c> (so the image
/// reports the build it is running, #2555); <c>plugin-test-image</c> does not. Same commit, same
/// runner, same architecture — and every core assembly inside <c>memex-portal-ai</c> carried
/// <c>AssemblyInformationalVersion 3.0.0-rc9.ci.&lt;run&gt;+&lt;sha&gt;</c> while the same assembly
/// inside <c>mw-plugin-test</c> carried <c>3.0.0-rc9+&lt;sha&gt;</c>. An assembly attribute is part
/// of the REFERENCE assembly, which is exactly what <c>meshweaver-surface.manifest</c> hashes, so
/// all 25 shared canonical surface hashes differed and the two images resolved different framework
/// identities (<c>sb0f6a11…</c> vs <c>s6dd7f50…</c> on run 33573271760). Every bake was published
/// to an address no portal asks for: INERT, #1814's shape, pods recompiling the whole mesh at
/// boot.</para>
///
/// <para><b>Why a guard and not just the fix.</b> The defect is invisible to every build: both
/// publishes succeed, both images ship, and only a hash nobody compares is wrong. CD's
/// <c>mw-plugin-test framework-identity … --expect …</c> step does catch it — but only after the
/// image set has been built and promoted, at the end of a 25-minute run, and only for the pair CD
/// happens to compare. This asserts the property itself, in seconds, on every PR.</para>
///
/// <para>See <c>Doc/Architecture/BakeIdentityMismatch</c> for the full reconstruction.</para>
/// </summary>
public class CompiledVersionAttributesIgnoreVersionOverrideGuard
{
    /// <summary>
    /// The project evaluated. Any project inheriting the root <c>Directory.Build.props</c> would
    /// do; this one is picked because its assembly is part of the canonical content surface (see
    /// <see cref="ProbedAssemblyIsPartOfTheContentSurface"/>), so the guard is measuring a build
    /// whose attributes really do feed the framework identity.
    /// </summary>
    private const string ProbeProject = "src/MeshWeaver.ShortGuid/MeshWeaver.ShortGuid.csproj";

    private const string ProbeAssembly = "MeshWeaver.ShortGuid";

    /// <summary>
    /// A version that could never be produced by <c>Directory.Build.props</c> itself, so its
    /// appearance in a compiled attribute is unambiguous evidence that the override leaked.
    /// </summary>
    private const string ProbeVersion = "9.9.9-guard.ci.424242";

    /// <summary>The three properties that become attributes inside the emitted (and reference) assembly.</summary>
    private static readonly string[] CompiledAttributes =
        ["InformationalVersion", "AssemblyVersion", "FileVersion"];

    /// <summary>
    /// The measurement: evaluate the same project twice — once bare, once with
    /// <c>-p:Version=</c> — and require the compiled attributes to be identical.
    /// </summary>
    [Fact]
    public void SupplyingAVersionDoesNotChangeAnyCompiledVersionAttribute()
    {
        var bare = Evaluate();
        var overridden = Evaluate($"-p:Version={ProbeVersion}");

        // 🚨 Non-empty in BOTH, asserted first. The regression this guard exists for evaluated the
        // three properties to the EMPTY STRING (the SDK fills them in a later target, from
        // $(Version)), so a bare equality check would have compared "" with "" and passed while
        // shipping the defect.
        foreach (var name in CompiledAttributes)
        {
            Assert.False(string.IsNullOrWhiteSpace(bare[name]),
                $"{name} evaluated empty for {ProbeProject} with no -p:Version. The root "
                + "Directory.Build.props no longer sets it, so the SDK will derive it from "
                + "$(Version) — the run-numbered string — and two publishes of one commit will "
                + "fork the framework identity (#3022).");
            Assert.False(string.IsNullOrWhiteSpace(overridden[name]),
                $"{name} evaluated empty for {ProbeProject} once -p:Version was supplied. That is "
                + "the exact #3022 regression: the PropertyGroup deriving the compiled version "
                + "attributes is guarded on '$(Version)' == '' again, so an explicit -p:Version "
                + "hands them to the SDK's $(Version)-derived defaults.");
        }

        var drifted = CompiledAttributes
            .Where(name => !string.Equals(bare[name], overridden[name], StringComparison.Ordinal))
            .Select(name => $"{name}: '{bare[name]}' -> '{overridden[name]}'")
            .ToList();

        Assert.True(drifted.Count == 0,
            "Supplying -p:Version= changed a COMPILED version attribute of " + ProbeProject + ":\n  "
            + string.Join("\n  ", drifted)
            + "\n\nThese attributes are emitted into the assembly and therefore into its REFERENCE "
            + "assembly, which meshweaver-surface.manifest hashes into the framework build "
            + "identity. CD publishes the portal image WITH -p:Version= and the bake/tester image "
            + "WITHOUT it, so any dependence here makes the two images of one commit resolve "
            + "different identities and every published bake INERT (#3022, #1814). Derive the "
            + "compiled attributes from $(PlatformVersion) unconditionally; -p:Version= must move "
            + "$(Version) — the package version, image tag and MESHWEAVER_PLATFORM_VERSION — and "
            + "nothing else.");

        var leaked = CompiledAttributes
            .Where(name => bare[name].Contains(ProbeVersion, StringComparison.Ordinal)
                           || overridden[name].Contains(ProbeVersion, StringComparison.Ordinal))
            .ToList();

        Assert.True(leaked.Count == 0,
            $"The supplied version string reached a compiled attribute ({string.Join(", ", leaked)}). "
            + "Same defect as above; see #3022.");
    }

    /// <summary>
    /// 🚨 The control arm. Without it the test above passes whenever the <c>-p:</c> never reached
    /// MSBuild at all — a mistyped switch, a renamed project, an evaluation that silently failed —
    /// which is a guard measuring nothing, dressed as a green tick. <c>$(Version)</c> is the ONE
    /// property the override is supposed to move, so it must differ across the two runs, and the
    /// bare run must produce the repo's own scheme rather than an SDK default.
    /// </summary>
    [Fact]
    public void TheOverrideActuallyReachedTheEvaluation()
    {
        var bare = Evaluate()["Version"];
        var overridden = Evaluate($"-p:Version={ProbeVersion}")["Version"];

        Assert.Equal(ProbeVersion, overridden);
        Assert.NotEqual(ProbeVersion, bare);
        Assert.StartsWith("3.", bare, StringComparison.Ordinal);
    }

    /// <summary>
    /// The probed project's assembly must be part of the canonical content surface — the set whose
    /// per-assembly surface hashes ARE the framework build identity. If it ever leaves that set the
    /// guard would still measure a real MSBuild evaluation, but no longer one that can fork a bake
    /// address, so it must be re-pointed rather than quietly kept.
    /// </summary>
    [Fact]
    public void ProbedAssemblyIsPartOfTheContentSurface()
        => Assert.Contains(ProbeAssembly, FrameworkBuildIdentity.ContentSurfaceAssemblies);

    /// <summary>
    /// Evaluates <see cref="ProbeProject"/> and returns the requested properties. Fails RED —
    /// never skips — when the project is missing, <c>dotnet</c> cannot be launched, MSBuild exits
    /// non-zero, or the answer is not the JSON shape <c>-getProperty</c> promises: "could not
    /// measure" must never be reported as "measured and fine".
    /// </summary>
    private static IReadOnlyDictionary<string, string> Evaluate(params string[] extraArguments)
    {
        var root = FindRepoRoot();
        var project = Path.Combine(root, ProbeProject.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(project),
            $"{ProbeProject} does not exist — this guard evaluates it to prove that -p:Version= "
            + "cannot reach a compiled attribute. Re-point it at another project that inherits the "
            + "root Directory.Build.props and is part of FrameworkBuildIdentity.ContentSurfaceAssemblies.");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("msbuild");
        psi.ArgumentList.Add(project);
        psi.ArgumentList.Add("-nologo");
        // CIRun is how the platform is built everywhere the identity matters; evaluating without it
        // would exercise the local-dev branch and prove nothing about CD.
        psi.ArgumentList.Add("-p:CIRun=true");
        foreach (var name in CompiledAttributes.Append("Version"))
            psi.ArgumentList.Add($"-getProperty:{name}");
        foreach (var argument in extraArguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        // `-getProperty` only EVALUATES (no restore, no compile) — sub-second warm, a few seconds
        // cold. A minute is far past any legitimate evaluation, so hitting it means MSBuild is
        // wedged, and the guard says so instead of hanging out xUnit's method timeout.
        Assert.True(process.WaitForExit(60_000),
            $"`dotnet msbuild {ProbeProject} -getProperty:…` did not finish within 60s. Evaluation "
            + "does not restore or build, so this is a wedged MSBuild, not a slow one.");

        Assert.True(process.ExitCode == 0,
            $"`dotnet msbuild {ProbeProject} -getProperty:…` exited {process.ExitCode}.\n"
            + $"stdout:\n{stdout}\nstderr:\n{stderr}");

        using var document = JsonDocument.Parse(stdout);
        var properties = document.RootElement.GetProperty("Properties");
        return CompiledAttributes.Append("Version")
            .ToDictionary(name => name, name => properties.GetProperty(name).GetString() ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException("Could not locate the repository root (no MeshWeaver.slnx above the test binary).");
    }
}
