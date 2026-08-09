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
    /// <summary>
    /// 🚨 Deliberately caps its OWN floor far below the 90 s default — the one class in the
    /// assembly allowed to, and exempted by name in the guard below.
    ///
    /// <para>The invariant under test is a RELATION, not a magic number:
    /// <c>EffectiveHardDeadline = max(TestHardDeadline, declared + HardDeadlineMargin)</c>, so a
    /// test that runs past its floor but inside its declared budget must survive. Exercising that
    /// at the real 90 s floor cost a 95 s sleep — the single slowest test in the entire suite by
    /// more than 2x (95.1 s; the runner-up is 49 s). At a 5 s floor with a 30 s declared budget
    /// the effective deadline is 60 s and an 8 s sleep proves exactly the same thing: it crosses
    /// the floor (5 s) and stays inside the raised deadline (60 s). Pre-fix it would be killed at
    /// 5 s, exactly as it used to be killed at 90 s.</para>
    ///
    /// <para>What is given up: this no longer pins the literal 90 s. That is covered structurally
    /// and for EVERY class by the static guard below, at zero runtime cost — which is the better
    /// place for it anyway.</para>
    /// </summary>
    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(5);

    /// <summary>
    /// This test declares a budget above its own floor and deliberately runs past that floor.
    /// Before the fix the watchdog failed it at the floor; now the declared budget is honoured.
    /// The sleep is the POINT of the test — it is what crosses the deadline — not a wait for a
    /// condition, so it is not the forbidden <c>Task.Delay</c>-to-await-propagation pattern.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task TestRunningPastTheDefaultDeadline_IsNotKilled_WhenItDeclaresALargerBudget()
    {
        await Task.Delay(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
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

            // 🚨 The ONE sanctioned exemption: this class exists to prove the watchdog raises the
            // floor to honour a larger declared budget, so it must cap its own floor BELOW its
            // declared [Fact(Timeout)] — the exact shape this guard rejects everywhere else.
            // Exempted by identity, not by a flag, so adding a second exemption is a deliberate
            // edit here rather than an attribute someone can sprinkle to silence the guard.
            if (type == typeof(HardDeadlineHonoursFactTimeoutTest))
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
