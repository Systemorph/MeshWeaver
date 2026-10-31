using System.Reflection;
using System.Text.Json;
using Xunit.v3;

namespace MeshWeaver.Testing.Xunit;

/// <summary>
/// 🚨 The per-test bound the estate's <c>xunit.runner.json</c> declares as <c>methodTimeout</c>, made
/// REAL (MeshWeaver.Plugins#4424).
///
/// <para><b>xunit.v3 has no <c>methodTimeout</c> setting.</b> Its configuration reader
/// (<c>ConfigReader_Json</c>, 4.0.2) knows no such key and drops it without a word, so a test that
/// declares no <c>[Fact(Timeout = …)]</c> runs with NO bound at all. Measured: a plain <c>[Fact]</c>
/// awaiting 8 s passed under <c>"methodTimeout": 2000</c>, while a <c>[Fact(Timeout = 2000)]</c>
/// control in the same run failed at 2 s. A test that never returns therefore ran to the host's
/// wall-clock cap and killed the whole suite with no verdict: <c>MeshWeaver.AI.Test</c> on run
/// 39850826367, 14 minutes held by one test whose name only the phase trace recorded.</para>
///
/// <para><b>What this does.</b> A test case xunit built with no timeout of its own is given the bound
/// before the run starts. From then on it is xunit's own, documented timeout: the test is FAILED BY
/// NAME at the bound (<c>Test execution timed out after N milliseconds</c>), its token is cancelled,
/// and the run continues. A test that declares its own <c>Timeout</c> keeps it.</para>
///
/// <para><b>Where the bound comes from.</b> <see cref="EnvironmentOverride"/> when it is set to a
/// positive number of milliseconds (a local run, or a test of this mechanism, dials it without
/// editing the tree), otherwise the assembly's <c>xunit.runner.json</c> <c>methodTimeout</c> — the
/// literal <c>TestTimeoutsTest.EveryRunnerConfigBoundsAWaitThatCanActuallyReport</c> already holds
/// above every shared wait budget. A config with no <c>methodTimeout</c> applies no bound, as before.</para>
/// </summary>
public static class MethodBound
{
    /// <summary>The environment variable that overrides the configured bound, in milliseconds.</summary>
    public const string EnvironmentOverride = "MESHWEAVER_TEST_METHOD_TIMEOUT_MS";

    /// <summary>The <c>xunit.runner.json</c> key the bound is read from.</summary>
    public const string ConfigKey = "methodTimeout";

    /// <summary>
    /// The bound for <paramref name="assembly"/>, in milliseconds, or 0 when none is declared.
    /// </summary>
    /// <param name="assembly">The assembly under test (its config file and location are read).</param>
    public static int Resolve(IXunitTestAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (int.TryParse(Environment.GetEnvironmentVariable(EnvironmentOverride), out var overridden) && overridden > 0)
            return overridden;
        foreach (var path in ConfigCandidates(assembly))
        {
            if (!File.Exists(path))
                continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            // The FIRST config that exists is the one xunit reads; a missing key there means no bound,
            // never "look further".
            return doc.RootElement.TryGetProperty(ConfigKey, out var value)
                   && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var ms) && ms > 0
                ? ms
                : 0;
        }
        return 0;
    }

    /// <summary>The config files xunit itself consults, in its order.</summary>
    private static IEnumerable<string> ConfigCandidates(IXunitTestAssembly assembly)
    {
        if (!string.IsNullOrEmpty(assembly.ConfigFilePath))
        {
            yield return assembly.ConfigFilePath;
            yield break;
        }
        var directory = Path.GetDirectoryName(assembly.AssemblyPath);
        if (string.IsNullOrEmpty(directory))
            yield break;
        yield return Path.Combine(directory, Path.GetFileNameWithoutExtension(assembly.AssemblyPath) + ".xunit.runner.json");
        yield return Path.Combine(directory, "xunit.runner.json");
    }

    /// <summary>
    /// Gives every test case that declares no timeout the bound. Returns how many were bounded.
    /// Throws when a case cannot be bounded, so an unbounded run is a red, named start-up error and
    /// never a silent one.
    /// </summary>
    /// <param name="testCases">The cases about to run.</param>
    /// <param name="boundMilliseconds">The bound; 0 leaves every case as it is.</param>
    public static int Apply(IEnumerable<IXunitTestCase> testCases, int boundMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(testCases);
        if (boundMilliseconds <= 0)
            return 0;
        var bounded = 0;
        foreach (var testCase in testCases)
        {
            if (testCase.Timeout > 0)
                continue;
            // xunit builds every [Fact] / [Theory] case (pre-enumerated rows and the delay-enumerated
            // theory alike) as an XunitTestCase, whose tests take their timeout from the case unless a
            // data row declares its own. The setter is not public: xunit offers no other way to give a
            // case a default timeout (its timeout is read only from the attribute).
            if (testCase is not XunitTestCase concrete)
                throw new InvalidOperationException(
                    $"'{testCase.TestCaseDisplayName}' is a {testCase.GetType().FullName}, not an XunitTestCase, so the "
                    + $"{boundMilliseconds} ms per-test bound cannot be applied to it. Give it its own Timeout, or extend "
                    + $"{nameof(MethodBound)} to bound that case type.");
            TimeoutSetter.Invoke(concrete, [boundMilliseconds]);
            bounded++;
        }
        return bounded;
    }

    private static readonly MethodInfo TimeoutSetter =
        typeof(XunitTestCase).GetProperty(nameof(XunitTestCase.Timeout))?.GetSetMethod(nonPublic: true)
        ?? throw new InvalidOperationException(
            "XunitTestCase.Timeout has no setter in this xunit.v3 version, so the per-test bound cannot be applied. "
            + "Re-measure how this version builds a case's timeout before upgrading.");
}
