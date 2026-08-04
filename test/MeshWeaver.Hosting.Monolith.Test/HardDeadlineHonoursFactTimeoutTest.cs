using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the watchdog invariant that <see cref="MonolithMeshTestBase"/> documents:
/// <i>the hard deadline MUST stay strictly above every in-test operation budget</i>.
///
/// <para>It used to be per-class discipline, and roughly a dozen classes broke it — declaring
/// <c>[Fact(Timeout = 120_000 … 300_000)]</c> while inheriting the 90 s default. The watchdog then
/// killed the test at 90 s mid-wait and blamed the author's CancellationToken handling, hiding the
/// operation that actually ran long. It only bit on CI, where an operation genuinely uses its
/// budget (a cold Roslyn NodeType compile on a fresh runner).</para>
/// </summary>
public class HardDeadlineHonoursFactTimeoutTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>
    /// This test declares a budget well above the 90 s default and deliberately runs past that
    /// default. Before the fix the watchdog failed it at 90 s; now the declared budget is honoured.
    /// The sleep is the POINT of the test — it is what crosses the old deadline — not a wait for a
    /// condition, so it is not the forbidden <c>Task.Delay</c>-to-await-propagation pattern.
    /// </summary>
    [Fact(Timeout = 150_000)]
    public async Task TestRunningPastTheDefaultDeadline_IsNotKilled_WhenItDeclaresALargerBudget()
    {
        await Task.Delay(TimeSpan.FromSeconds(95), TestContext.Current.CancellationToken);
        Assert.True(true, "the watchdog must honour this test's declared [Fact(Timeout)]");
    }

    /// <summary>
    /// Static guard: no class in this assembly may declare a <c>[Fact(Timeout)]</c> the watchdog
    /// would not honour. This is what actually prevents recurrence — the runtime derivation fixes
    /// the behaviour, and this fails the build-out if someone reintroduces the contradiction by
    /// overriding <c>TestHardDeadline</c> DOWNWARD below a declared budget.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void NoTestClass_OverridesHardDeadlineBelowItsOwnDeclaredFactTimeout()
    {
        var offenders = new List<string>();

        foreach (var type in typeof(HardDeadlineHonoursFactTimeoutTest).Assembly.GetTypes()
                     .Where(t => t is { IsAbstract: false, IsClass: true }
                                 && typeof(MonolithMeshTestBase).IsAssignableFrom(t)))
        {
            var declared = type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(m => m.GetCustomAttribute<FactAttribute>()?.Timeout ?? 0)
                .DefaultIfEmpty(0)
                .Max();
            if (declared <= 0)
                continue;

            // Only an EXPLICIT override can now under-cut a declared budget; the inherited
            // default is raised automatically by EffectiveHardDeadline.
            var overrideProp = type.GetProperty("TestHardDeadline",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (overrideProp?.GetMethod?.DeclaringType != type)
                continue;

            var instance = System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(type);
            var configured = (TimeSpan)overrideProp.GetValue(instance)!;
            if (configured.TotalMilliseconds < declared)
                offenders.Add(
                    $"{type.Name}: TestHardDeadline={configured.TotalSeconds:F0}s < " +
                    $"declared [Fact(Timeout)]={declared / 1000.0:F0}s");
        }

        Assert.True(offenders.Count == 0,
            "a class must never cap the watchdog below a budget its own tests declare — "
            + "the watchdog would kill the test mid-wait and misattribute the cause:\n  "
            + string.Join("\n  ", offenders));
    }
}
