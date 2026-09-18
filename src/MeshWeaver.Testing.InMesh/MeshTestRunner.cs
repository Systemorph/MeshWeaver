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
/// </summary>
public static class MeshTestRunner
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

    /// <summary>The <c>Tests</c> area over the given classes.</summary>
    public static IObservable<UiControl?> Area(LayoutAreaHost host, string suite, IEnumerable<Type> classes, TimeSpan? deadline = null) =>
        Run(host, classes, deadline ?? DefaultDeadline)
            .ToList()
            .Select(results => (UiControl?)Render(suite, results.ToList()));

    /// <summary>Executes the cases, one at a time, emitting each verdict as it lands.</summary>
    /// <remarks>A null host runs the classes that need no mesh (parameterless constructors) — the runner's own tests use it.</remarks>
    public static IObservable<CaseResult> Run(LayoutAreaHost? host, IEnumerable<Type> classes, TimeSpan deadline) =>
        classes.Select(cls => RunClass(host, cls, deadline)).Concat();

    private static IObservable<CaseResult> RunClass(LayoutAreaHost? host, Type cls, TimeSpan deadline)
    {
        var partition = $"{MeshTestContext.TestRoot}/{cls.Name}-{Guid.NewGuid():N}"[..Math.Min(80, MeshTestContext.TestRoot.Length + 1 + cls.Name.Length + 33)];
        var cases = Cases(cls).ToList();
        return Observable.Defer(() =>
        {
            var output = new List<string>();
            var context = host is null ? null : new MeshTestContext(host, partition, output.Add, deadline);
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
            var pool = host?.Hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Tests) ?? IoPool.Unbounded;
            return cases.Select(c => RunCase(instance, cls, c, output, deadline, context, pool)).Concat();
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

    private static IObservable<CaseResult> RunCase(object? instance, Type cls, TestCase c, List<string> output, TimeSpan deadline, MeshTestContext? context, IIoPool pool)
    {
        if (c.Skip is not null)
            return Observable.Return(new CaseResult(cls.Name, c.Name, "⏭ skipped", c.Skip, TimeSpan.Zero));
        var bound = c.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(c.TimeoutSeconds) : deadline;
        var started = DateTimeOffset.UtcNow;
        output.Clear();
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
                .Select(_ => Fail($"no verdict within {bound.TotalSeconds:F0}s — cancelled and unwound"))
                .Catch<CaseResult, TimeoutException>(_ => Observable.Return(Fail(
                    $"no verdict within {bound.TotalSeconds:F0}s — and the case IGNORED its cancellation token: still running {CancellationGrace.TotalSeconds:F0}s after it was cancelled. Pass MeshTestContext.CancellationToken (or a trailing CancellationToken parameter) into what the case awaits"))))
            .Catch<CaseResult, Exception>(ex => Observable.Return(Fail(Unwrap(ex).Message)));
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

    /// <summary>The gate's contract: a title carrying "N/M passed" and a ✅/❌ table.</summary>
    public static UiControl Render(string suite, IReadOnlyList<CaseResult> results)
    {
        return Controls.Stack.WithWidth("100%")
            .WithView(Controls.Title(Summary(suite, results), 2), "Title")
            .WithView(Controls.Markdown(Table(results)), "Cases");
    }

    private static string Escape(string s) => s.Replace("|", "\\|").Replace("\n", " ");
}
