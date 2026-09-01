using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace MeshWeaver.PluginTester;

/// <summary>
/// Executes a compiled NodeType's in-mesh tests WITHOUT a mesh: the <c>Test/*.cs</c> convention is
/// static classes whose public static parameterless methods throw on failure (that is what a
/// <c>Tests</c> layout area's case table wraps), so the assembly the build just emitted can be
/// loaded into a collectible context and those methods invoked directly, each one timed.
///
/// <para><b>What this runs and what it reports as not-run.</b> A case that needs a host — a method
/// with parameters (<c>LayoutAreaHost</c>, a hub) — cannot execute here; it is COUNTED as
/// <see cref="Outcome.NeedsMesh"/> and named, never silently dropped, so the report says exactly
/// how much of a type's suite the mesh lane still owns. That is the honest split the maintainer
/// set on 2026-08-30: compile and pure tests off the mesh ("grains cannot handle compile
/// workload"; "do not try to import mesh nodes"), the hosted cases through the gate that seeds
/// from this build's output.</para>
///
/// <para><b>The xUnit-shaped surface a case may use.</b> There is no xUnit in this process, so the
/// runner supplies the three things a migrated body actually reaches for, and nothing more:
/// <see cref="Testing.TestContext"/><c>.Current</c> (the case's name and a token that trips just
/// inside its budget), <see cref="Testing.SkipException"/> (reported as
/// <see cref="Outcome.Skipped"/> with its reason — never as passed and never as failed), and
/// <see cref="Testing.TestLog"/> (captured PER CASE and attached to
/// <see cref="Case.Log"/>, printed with the case when it fails). Deliberately not a second xUnit:
/// no attributes, no fixtures, no theories, no assertion library.</para>
///
/// <para><b>Resolution.</b> The emitted assembly binds the framework (already in this process —
/// the tester's own closure is the image's <c>/app</c>) and the dependency packages' emitted
/// assemblies, which are mapped by simple name into the context. Anything else is a missing
/// dependency and surfaces as the load failure it is.</para>
/// </summary>
public static class StaticTestRunner
{
    /// <summary>What happened to one case.</summary>
    public enum Outcome
    {
        /// <summary>Ran and returned.</summary>
        Passed,
        /// <summary>Ran and threw.</summary>
        Failed,
        /// <summary>Needs a host or hub — left to the mesh lane.</summary>
        NeedsMesh,
        /// <summary>
        /// Ran and declined: the body threw <see cref="Testing.SkipException"/>, naming a condition
        /// it cannot meet here. 🚨 <b>Never folded into <see cref="Passed"/>.</b> A skip asserted
        /// NOTHING, so counting it as a pass makes a verdict line claim evidence that was never
        /// produced — the "absence of evidence reads as green" defect this estate keeps paying for.
        /// It does not fail the run either: declining is a legitimate answer, and it is REPORTED
        /// with its reason so a reader can see how much of a suite actually ran.
        /// </summary>
        Skipped,
    }

    /// <summary>One executed (or classified) case.</summary>
    /// <param name="Name"><c>Type.Method</c>.</param>
    /// <param name="Outcome">The verdict.</param>
    /// <param name="Elapsed">Wall time of the invocation (zero when not run).</param>
    /// <param name="Error">The failure text, innermost exception first; the reason for a skip.</param>
    public sealed record Case(string Name, Outcome Outcome, TimeSpan Elapsed, string? Error)
    {
        /// <summary>
        /// What the case wrote through <see cref="Testing.TestLog"/> while it ran, in order.
        /// Captured per case (never through a process-wide sink — packages build in parallel), and
        /// printed with the case when it FAILS, including when it is abandoned for outliving its
        /// budget: the lines up to the hang are usually the only evidence of where it got to.
        /// </summary>
        public ImmutableArray<string> Log { get; init; } = [];
    }

    /// <summary>The run over one assembly.</summary>
    /// <param name="Assembly">The assembly path.</param>
    /// <param name="Cases">Every case found, in discovery order.</param>
    /// <param name="LoadError">Set when the assembly could not be loaded at all — no cases then.</param>
    public sealed record Run(string Assembly, ImmutableArray<Case> Cases, string? LoadError)
    {
        /// <summary>Cases that ran and passed.</summary>
        public int Passed => Cases.Count(c => c.Outcome == Outcome.Passed);

        /// <summary>Cases that ran and failed.</summary>
        public int Failed => Cases.Count(c => c.Outcome == Outcome.Failed);

        /// <summary>Cases that need the mesh lane.</summary>
        public int NeedsMesh => Cases.Count(c => c.Outcome == Outcome.NeedsMesh);

        /// <summary>
        /// Cases that ran and DECLINED (<see cref="Outcome.Skipped"/>). 🚨 Reported separately from
        /// <see cref="Passed"/> everywhere, because a verdict of "12/12 passed" over a suite where
        /// three declined is a false claim about what was proven.
        /// </summary>
        public int Skipped => Cases.Count(c => c.Outcome == Outcome.Skipped);

        /// <summary>
        /// Green iff it loaded and nothing that ran failed. No cases is green, not a pass — and
        /// neither is a skip: <see cref="Skipped"/> does not turn the run red (declining is a
        /// legitimate answer) but it is never counted as evidence, which is why it has its own
        /// column in every report rather than being absorbed here.
        /// </summary>
        public bool IsGreen => LoadError is null && Failed == 0;
    }

    /// <summary>
    /// Loads <paramref name="assemblyPath"/> with <paramref name="dependencyAssemblies"/>
    /// resolvable by simple name, discovers the test classes, invokes every runnable case and
    /// unloads the context. The <paramref name="perCaseTimeout"/> is a hard cap: a case that
    /// outlives it is reported failed with the timeout named, and the run continues.
    /// </summary>
    public static Run Execute(
        string assemblyPath,
        IReadOnlyList<string> dependencyAssemblies,
        TimeSpan perCaseTimeout,
        TextWriter? output = null)
    {
        ArgumentNullException.ThrowIfNull(assemblyPath);
        ArgumentNullException.ThrowIfNull(dependencyAssemblies);

        var byName = dependencyAssemblies
            .Where(File.Exists)
            .GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var context = new TestLoadContext(byName);
        try
        {
            Assembly assembly;
            try
            {
                assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or FileLoadException)
            {
                return new Run(assemblyPath, [], $"{ex.GetType().Name}: {ex.Message}");
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var missing = ex.LoaderExceptions
                    .Where(e => e is not null)
                    .Select(e => e!.Message)
                    .Distinct(StringComparer.Ordinal)
                    .Take(3);
                return new Run(assemblyPath, [],
                    "the assembly loaded but its types did not: " + string.Join(" | ", missing)
                    + " — a dependency package's assembly is not in the reference set handed to "
                    + "this run, or a module named by the source is not composed into the image");
            }

            var cases = ImmutableArray.CreateBuilder<Case>();
            foreach (var type in types.Where(IsTestClass).OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                foreach (var method in TestMethods(type))
                {
                    var name = $"{type.Name}.{method.Name}";
                    if (method.GetParameters().Length > 0)
                    {
                        cases.Add(new Case(name, Outcome.NeedsMesh, TimeSpan.Zero,
                            "takes " + string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))
                            + " — a hosted case; the mesh lane runs it"));
                        continue;
                    }
                    cases.Add(Invoke(name, method, perCaseTimeout));
                    Report(output, cases[^1]);
                }
            }
            return new Run(assemblyPath, cases.ToImmutable(), null);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// Writes one case's line to the build log, and — for a FAILURE only — whatever the case wrote
    /// through <c>TestLog</c> before it died. A pass needs no narration and a skip already carries
    /// its reason; a failure is the one place the neighbourhood earns its space.
    /// </summary>
    private static void Report(TextWriter? output, Case c)
    {
        if (output is null)
            return;
        output.WriteLine(Describe(c));
        if (c.Outcome != Outcome.Failed)
            return;
        foreach (var line in c.Log)
            output.WriteLine(line);
    }

    private static string Describe(Case c) => c.Outcome switch
    {
        Outcome.Passed => $"      ok    {c.Name} ({c.Elapsed.TotalMilliseconds:F0} ms)",
        Outcome.Failed => $"      FAIL  {c.Name} ({c.Elapsed.TotalMilliseconds:F0} ms): {c.Error}",
        // 🚨 Its own token. A skip printed as `ok` is a claim that the case proved something.
        Outcome.Skipped => $"      SKIP  {c.Name} ({c.Elapsed.TotalMilliseconds:F0} ms): {c.Error}",
        _ => $"      mesh  {c.Name}: {c.Error}",
    };

    private static Case Invoke(string name, MethodInfo method, TimeSpan timeout)
    {
        // Per-case capture, never a process-wide sink: the cascade builds packages in PARALLEL, so
        // two cases are routinely in flight at once and a shared collector would attribute one's
        // output to the other. Locked because the runner thread reads it while an ABANDONED case
        // thread may still be writing to it (the hung-case path below).
        var log = new List<string>();
        void Capture(string line) { lock (log) log.Add(line); }
        ImmutableArray<string> Captured() { lock (log) return [.. log]; }

        // The budget as a token, so a case that threads TestContext.Current.CancellationToken
        // through its waits ends with a NAMED cancellation instead of being abandoned. It trips a
        // little before the join deadline for exactly that reason — a token that fires at the same
        // instant Join gives up could never be acted on, and the hard cap stays at `timeout`.
        var budget = new CancellationTokenSource();
        var lead = timeout > TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(Math.Min(1000, timeout.TotalMilliseconds / 10))
            : TimeSpan.Zero;

        var clock = Stopwatch.StartNew();
        // The case runs on its own thread so a hang can be reported by name rather than hanging
        // the whole build; the thread is background so it cannot keep the process alive.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            // Entered and disposed on the CASE's thread — the context is [ThreadStatic], so this is
            // the only place it can be installed and the only place it can be cleared.
            using var _ = Testing.TestContext.Enter(
                new Testing.TestContextData(name, budget.Token) { Log = Capture });
            try
            {
                method.Invoke(null, null);
            }
            catch (TargetInvocationException ex)
            {
                failure = ex.InnerException ?? ex;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = $"test:{name}",
        };
        thread.Start();
        if (timeout > TimeSpan.Zero)
            budget.CancelAfter(timeout - lead);
        // 🚨 Thread.Join(timeout), not a ManualResetEventSlim. It is the built-in primitive for
        // exactly this — "did that thread finish within the budget" — and it is STRICTER: the event
        // fired from a `finally` signalled before the thread had actually terminated, whereas Join
        // returns only on real termination, which is also what gives `failure` its happens-before.
        // A hand-woven gate here was flagged by HandWovenGateRatchetGuard; the fix is to delete the
        // gate rather than exempt it, because the standard library already has this one.
        if (!thread.Join(timeout))
        {
            // 🚨 `budget` is deliberately NOT disposed here. The abandoned thread is still running
            // and still holds its token; disposing the source under it is a use-after-dispose, and
            // in this estate that surfaces as an exit-139 nobody can reproduce. It is left to the
            // GC, bounded by the timer CancelAfter already scheduled.
            return new Case(name, Outcome.Failed, clock.Elapsed,
                $"did not return within {timeout.TotalSeconds:F0}s — a hung case; the thread is "
                + "abandoned and the build continues")
            { Log = Captured() };
        }
        clock.Stop();
        var expired = budget.IsCancellationRequested;
        // Safe only because Join returned: the case thread has really terminated, so nothing holds
        // the token any more.
        budget.Dispose();
        var captured = Captured();
        return failure switch
        {
            null => new Case(name, Outcome.Passed, clock.Elapsed, null) { Log = captured },
            // A case that DID observe its budget: say so, because `Innermost` would otherwise
            // report the framework's "The operation was canceled." — true and useless.
            OperationCanceledException when expired =>
                new Case(name, Outcome.Failed, clock.Elapsed,
                    $"OperationCanceledException: the case budget ({timeout.TotalSeconds:F0}s) "
                    + "expired and the case ended on TestContext.Current.CancellationToken")
                { Log = captured },
            // 🚨 The UNWRAPPED exception only — never a walk down the inner chain. A skip is
            // something the case says about itself; an assertion failure that happens to carry a
            // SkipException as an inner would otherwise be reported as a decision not to run.
            Testing.SkipException skip =>
                new Case(name, Outcome.Skipped, clock.Elapsed, skip.Reason) { Log = captured },
            _ => new Case(name, Outcome.Failed, clock.Elapsed, Innermost(failure)) { Log = captured },
        };
    }

    private static string Innermost(Exception ex)
    {
        var e = ex;
        while (e.InnerException is not null)
            e = e.InnerException;
        var first = e.Message.Split('\n', 2)[0].Trim();
        return $"{e.GetType().Name}: {first}";
    }

    /// <summary>
    /// A test class: public, static (abstract + sealed), not compiler-generated, whose name ends
    /// in <c>Test</c> or <c>Tests</c> — the convention every <c>Test/*.cs</c> in the node repos
    /// follows (<c>ModuleTests</c>, <c>CourseIndexTests</c>, …). The <c>*TestsArea</c> aggregator
    /// classes match too, and their only method takes a host, so they classify as needs-mesh.
    /// </summary>
    private static bool IsTestClass(Type t) =>
        t.IsClass && t.IsPublic && t.IsAbstract && t.IsSealed
        && !t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        && (t.Name.EndsWith("Tests", StringComparison.Ordinal)
            || t.Name.EndsWith("Test", StringComparison.Ordinal)
            || t.Name.EndsWith("TestsArea", StringComparison.Ordinal));

    private static IEnumerable<MethodInfo> TestMethods(Type t) =>
        t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName
                        && !m.IsGenericMethodDefinition
                        && !m.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
                        && (m.ReturnType == typeof(void) || m.GetParameters().Length > 0))
            .OrderBy(m => m.Name, StringComparer.Ordinal);

    private sealed class TestLoadContext(IReadOnlyDictionary<string, string> dependencyPaths)
        : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Framework and image assemblies: whatever this process already runs — the tester's
            // closure IS the image's /app, and binding a second copy would split every type.
            if (Default.Assemblies.Any(a => string.Equals(
                    a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase)))
                return null;
            return assemblyName.Name is { } name && dependencyPaths.TryGetValue(name, out var path)
                ? LoadFromAssemblyPath(path)
                : null;
        }
    }
}
