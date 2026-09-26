using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Testing.InMesh;

/// <summary>
/// Runs the <see cref="MeshFactAttribute"/> / <see cref="MeshTheoryAttribute"/> cases of a set of
/// classes against the live mesh and renders the verdict the plugin gate parses: a title
/// "<i>name</i> tests — N/M passed" and a ✅/❌/⏭ table. A NodeType's <c>Tests</c> area becomes ONE line:
/// <c>layout.WithView("Tests", (host, _) => MeshTestRunner.Area(host, "Hosting", typeof(SomeTest).Assembly))</c>.
/// Cases run one after another (the mesh is shared; a class gets its own partition); each is
/// bounded by a deadline and a failure carries the exception's message. Nothing here needs setup:
/// the mesh the area renders in is the fixture.
///
/// <para>The area STREAMS: it renders every case as pending the moment it is opened, then each
/// case as running (with its elapsed time and its output lines as they arrive) and finished, and
/// only the last frame carries the verdict — see <see cref="Progress(LayoutAreaHost?, IEnumerable{Type}, TimeSpan, IIoPool?, TimeSpan?)"/>.</para>
/// </summary>
public static partial class MeshTestRunner
{
    /// <summary>The default per-case deadline.</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(30);

    /// <summary>One executed case, for the table.</summary>
    public sealed record CaseResult(string Class, string Name, string Result, string Detail, TimeSpan Elapsed)
    {
        /// <summary>True for ✅.</summary>
        public bool Passed => Result.StartsWith("✅", StringComparison.Ordinal);
        /// <summary>True for ⏭.</summary>
        public bool Skipped => Result.StartsWith("⏭", StringComparison.Ordinal);
    }

    /// <summary>Every class of <paramref name="assembly"/> that declares at least one case.</summary>
    public static IReadOnlyList<Type> TestClasses(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && Cases(t).Any())
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    /// <summary>The <c>Tests</c> area over every test class of an assembly.</summary>
    public static IObservable<UiControl?> Area(LayoutAreaHost host, string suite, Assembly assembly, TimeSpan? deadline = null) =>
        Area(host, suite, TestClasses(assembly), deadline);

    /// <summary>
    /// The <c>Tests</c> area over the given classes: a progress frame per second while the cases
    /// run, then the verdict frame (see <see cref="Progress(LayoutAreaHost?, IEnumerable{Type}, TimeSpan, IIoPool?, TimeSpan?)"/>).
    /// </summary>
    public static IObservable<UiControl?> Area(LayoutAreaHost host, string suite, IEnumerable<Type> classes, TimeSpan? deadline = null) =>
        Frames(host, suite, Progress(host, classes, deadline ?? DefaultDeadline));

    /// <summary>
    /// The <c>Tests</c> area over explicitly listed cases — the shape of a Tests area whose cases
    /// are static methods rather than <see cref="MeshFactAttribute"/> classes. Streams exactly as
    /// the class-based area does.
    /// </summary>
    /// <param name="host">The area host.</param>
    /// <param name="suite">The suite name the verdict title carries.</param>
    /// <param name="cases">The cases, run one after another in this order.</param>
    /// <param name="deadline">The per-case bound when a case declares none.</param>
    public static IObservable<UiControl?> Area(LayoutAreaHost host, string suite, IReadOnlyList<MeshTestCase> cases, TimeSpan? deadline = null) =>
        Frames(host, suite, Progress(host, suite, cases, deadline ?? DefaultDeadline));

    /// <summary>Executes the cases, one at a time, emitting each verdict as it lands.</summary>
    /// <remarks>A null host runs the classes that need no mesh (parameterless constructors) — the runner's own tests use it.</remarks>
    /// <param name="host">The area host; null runs the classes that need no mesh.</param>
    /// <param name="classes">The test classes, run one after another.</param>
    /// <param name="deadline">The per-case bound when a case declares none.</param>
    /// <param name="pool">The pool the cases run on. Null resolves the mesh's <see cref="IoPoolNames.Tests"/>
    /// pool (or <see cref="IoPool.Unbounded"/> without a host); the runner's own tests pass a bounded one.</param>
    public static IObservable<CaseResult> Run(LayoutAreaHost? host, IEnumerable<Type> classes, TimeSpan deadline, IIoPool? pool = null) =>
        Run(host, classes, deadline, pool, NoSignal, NoLine);

    private static void NoSignal() { }

    private static void NoLine(string _) { }

    private static IObservable<CaseResult> Run(LayoutAreaHost? host, IEnumerable<Type> classes, TimeSpan deadline, IIoPool? pool, Action onStarted, Action<string> onLine) =>
        Observable.Defer(() =>
        {
            // The cases of THIS run that ignored their cancellation and are therefore still holding a
            // pool slot. Per run, never static: two Tests areas rendering at once do not share it.
            var leaked = new List<string>();
            return classes.Select(cls => RunClass(host, cls, deadline, pool, leaked, onStarted, onLine)).Concat();
        });

    private static IObservable<CaseResult> RunClass(LayoutAreaHost? host, Type cls, TimeSpan deadline, IIoPool? requestedPool, List<string> leaked, Action onStarted, Action<string> onLine)
    {
        var partition = $"{MeshTestContext.TestRoot}/{cls.Name}-{Guid.NewGuid():N}"[..Math.Min(80, MeshTestContext.TestRoot.Length + 1 + cls.Name.Length + 33)];
        var cases = Cases(cls).ToList();
        return Observable.Defer(() =>
        {
            var output = new List<string>();
            // Every line a case writes lands in its verdict's detail AND streams to the progress
            // frame the moment it is written — a slow case shows what it is doing while it does it.
            var context = host is null ? null : new MeshTestContext(host, partition, line => { output.Add(line); onLine(line); }, deadline);
            object? instance;
            MeshTestContext.Current = context;
            try
            {
                instance = Instantiate(cls, context);
            }
            catch (Exception ex)
            {
                var reason = $"the class could not be constructed: {Unwrap(ex).Message}";
                return cases.Select(c => new CaseResult(cls.Name, c.Name, "❌ FAIL", reason, TimeSpan.Zero)).ToObservable();
            }
            // A case is ONE leaf on the mesh's Tests pool — never Observable.FromAsync (an ABSOLUTE in
            // AGENTS.md: it runs the prologue on the subscribing thread with no bound). The pool links
            // the leaf's token to the subscription, which is what lets the bound below CANCEL a case
            // rather than abandon it. Host-less runs (the runner's own tests) have no registry.
            var pool = requestedPool ?? host?.Hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Tests) ?? IoPool.Unbounded;
            return cases.Select(c => RunCase(instance, cls, c, output, deadline, context, pool, leaked, onStarted)).Concat();
        });
    }

    /// <summary>
    /// How long the runner waits, after a case's bound elapsed and its token was cancelled, for the
    /// case to unwind before reporting that it IGNORED the token. A case that observes
    /// <see cref="MeshTestContext.CancellationToken"/> (or its trailing <see cref="CancellationToken"/>
    /// parameter) ends inside this grace; one that does not is still running when it ends, and the
    /// runner says so in the verdict — it cannot stop what the case started, only name it.
    /// </summary>
    public static readonly TimeSpan CancellationGrace = TimeSpan.FromSeconds(2);

    private static IObservable<CaseResult> RunCase(object? instance, Type cls, TestCase c, List<string> output, TimeSpan deadline, MeshTestContext? context, IIoPool pool, List<string> leaked, Action onStarted) =>
        // Deferred so the clock, the leak check and the "running" signal all belong to the moment
        // the case actually STARTS — not to the moment Concat asked for the observable.
        Observable.Defer(() => RunCaseNow(instance, cls, c, output, deadline, context, pool, leaked, onStarted));

    private static IObservable<CaseResult> RunCaseNow(object? instance, Type cls, TestCase c, List<string> output, TimeSpan deadline, MeshTestContext? context, IIoPool pool, List<string> leaked, Action onStarted)
    {
        if (c.Skip is not null)
            return Observable.Return(new CaseResult(cls.Name, c.Name, "⏭ skipped", c.Skip, TimeSpan.Zero));
        // 🚨 A case that ignored its cancellation still HOLDS its pool slot — the runner could name it,
        // not stop it. If such leaks have filled the pool, this case would queue behind them and die
        // as an anonymous "no verdict" without ever executing; say what is true instead, at once.
        if (leaked.Count > 0 && pool is IoPool bounded && bounded.CurrentInFlight >= bounded.MaxConcurrency)
            return Observable.Return(new CaseResult(cls.Name, c.Name, "❌ FAIL",
                $"not run — every slot of the Tests pool ({bounded.MaxConcurrency}) is held, {leaked.Count} of them by case(s) of this run that ignored their cancellation and are still running: {string.Join(", ", leaked)}. Fix those cases; this one never executed",
                TimeSpan.Zero));
        var bound = c.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(c.TimeoutSeconds) : deadline;
        var started = DateTimeOffset.UtcNow;
        output.Clear();
        onStarted();
        // The leaf signals its own unwinding. When the bound elapses, Timeout disposes the leaf's
        // subscription, the pool cancels the token it handed the case, and the runner waits the grace
        // on this signal: a case that observed the token completes it; one that did not leaves it
        // pending — the shape xUnit1069 names on xunit, named here at run time instead.
        var unwound = new AsyncSubject<Unit>();
        var work = pool.Invoke(async ct =>
        {
            try
            {
                if (context is not null)
                    context.CancellationToken = ct;
                var result = c.Method.Invoke(instance, Arguments(c, ct));
                switch (result)
                {
                    case Task t: await t; break;
                    case ValueTask vt: await vt; break;
                }
                return Unit.Default;
            }
            finally
            {
                if (context is not null)
                    context.CancellationToken = CancellationToken.None;
                unwound.OnNext(Unit.Default);
                unwound.OnCompleted();
            }
        });
        CaseResult Fail(string detail) => new(cls.Name, c.Name, "❌ FAIL", detail + (output.Count > 0 ? " · " + string.Join(" · ", output) : ""), DateTimeOffset.UtcNow - started);
        return work
            .Timeout(bound)
            .Select(_ => new CaseResult(cls.Name, c.Name, "✅ pass", string.Join(" · ", output), DateTimeOffset.UtcNow - started))
            .Catch<CaseResult, TimeoutException>(_ => unwound
                .Timeout(CancellationGrace)
                .Select(_ => Fail($"{TimedOutPrefix}no verdict within {bound.TotalSeconds:F0}s — cancelled and unwound"))
                .Catch<CaseResult, TimeoutException>(_ => Observable.Return(Leak(leaked, $"{cls.Name}.{c.Name}", Fail(
                    $"{TimedOutPrefix}no verdict within {bound.TotalSeconds:F0}s — and the case IGNORED its cancellation token: still running {CancellationGrace.TotalSeconds:F0}s after it was cancelled. Pass MeshTestContext.CancellationToken (or a trailing CancellationToken parameter) into what the case awaits")))))
            .Catch<CaseResult, Exception>(ex => Observable.Return(Fail(Unwrap(ex).Message)));
    }

    /// <summary>Records a case that is still running after its cancellation, so a pool it fills can be NAMED.</summary>
    private static CaseResult Leak(List<string> leaked, string name, CaseResult verdict)
    {
        leaked.Add(name);
        return verdict;
    }

    /// <summary>The case's arguments, plus the runner's token when the method declares a trailing <see cref="CancellationToken"/> parameter.</summary>
    private static object?[] Arguments(TestCase c, CancellationToken ct)
    {
        var parameters = c.Method.GetParameters();
        return parameters.Length == c.Arguments.Length + 1 && parameters[^1].ParameterType == typeof(CancellationToken)
            ? [.. c.Arguments, ct]
            : c.Arguments;
    }

    private static object? Instantiate(Type cls, MeshTestContext? context)
    {
        var withContext = cls.GetConstructor([typeof(MeshTestContext)]);
        if (withContext is not null)
            return context is null ? throw new InvalidOperationException($"{cls.Name} needs the mesh (a MeshTestContext) and this run has none") : withContext.Invoke([context]);
        var empty = cls.GetConstructor(Type.EmptyTypes);
        if (empty is not null) return empty.Invoke([]);
        throw new InvalidOperationException($"{cls.Name} needs a constructor taking a MeshTestContext, or none");
    }

    private sealed record TestCase(string Name, MethodInfo Method, object?[] Arguments, string? Skip, int TimeoutSeconds);

    private static IEnumerable<TestCase> Cases(Type cls)
    {
        foreach (var m in cls.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var fact = m.GetCustomAttribute<MeshFactAttribute>();
            if (fact is not null)
            {
                yield return new TestCase(fact.DisplayName ?? m.Name, m, [], fact.Skip, fact.TimeoutSeconds);
                continue;
            }
            var theory = m.GetCustomAttribute<MeshTheoryAttribute>();
            if (theory is null) continue;
            var rows = m.GetCustomAttributes<MeshInlineDataAttribute>().ToList();
            if (rows.Count == 0)
                yield return new TestCase(theory.DisplayName ?? m.Name, m, [], theory.Skip ?? "a theory without inline data", theory.TimeoutSeconds);
            foreach (var row in rows)
                yield return new TestCase($"{theory.DisplayName ?? m.Name}({string.Join(", ", row.Data.Select(d => d?.ToString() ?? "null"))})", m, row.Data, theory.Skip, theory.TimeoutSeconds);
        }
    }

    private static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? Unwrap(inner)
        : ex is AggregateException { InnerExceptions.Count: 1 } agg ? Unwrap(agg.InnerExceptions[0]) : ex;

    /// <summary>The title line the gate parses: "&lt;suite&gt; tests — N/M passed".</summary>
    public static string Summary(string suite, IReadOnlyList<CaseResult> results)
    {
        var passed = results.Count(r => r.Passed);
        var total = results.Count(r => !r.Skipped);
        return $"{suite} tests — {passed}/{total} passed{(results.Count - total > 0 ? $" · {results.Count - total} skipped" : "")}";
    }

    /// <summary>The verdict table: one row per case.</summary>
    public static string Table(IReadOnlyList<CaseResult> results) =>
        "| Class | Case | Result | Time | Detail |\n|---|---|---|---:|---|\n"
        + string.Join("\n", results.Select(r => $"| {r.Class} | {Escape(r.Name)} | {r.Result} | {r.Elapsed.TotalSeconds:0.0}s | {Escape(r.Detail)} |"));

    /// <summary>The gate's contract: a title carrying "N/M passed" and a ✅/❌ table (English column titles).</summary>
    public static UiControl Render(string suite, IReadOnlyList<CaseResult> results) =>
        Render(suite, results, ColumnTitles.English);

    private static string Escape(string s) => s.Replace("|", "\\|").Replace("\n", " ");
}
