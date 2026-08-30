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
    }

    /// <summary>One executed (or classified) case.</summary>
    /// <param name="Name"><c>Type.Method</c>.</param>
    /// <param name="Outcome">The verdict.</param>
    /// <param name="Elapsed">Wall time of the invocation (zero when not run).</param>
    /// <param name="Error">The failure text, innermost exception first.</param>
    public sealed record Case(string Name, Outcome Outcome, TimeSpan Elapsed, string? Error);

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

        /// <summary>Green iff it loaded and nothing that ran failed. No cases is green, not a pass.</summary>
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
                    output?.WriteLine(Describe(cases[^1]));
                }
            }
            return new Run(assemblyPath, cases.ToImmutable(), null);
        }
        finally
        {
            context.Unload();
        }
    }

    private static string Describe(Case c) => c.Outcome switch
    {
        Outcome.Passed => $"      ok    {c.Name} ({c.Elapsed.TotalMilliseconds:F0} ms)",
        Outcome.Failed => $"      FAIL  {c.Name} ({c.Elapsed.TotalMilliseconds:F0} ms): {c.Error}",
        _ => $"      mesh  {c.Name}: {c.Error}",
    };

    private static Case Invoke(string name, MethodInfo method, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        // The case runs on its own thread so a hang can be reported by name rather than hanging
        // the whole build; the thread is background so it cannot keep the process alive.
        Exception? failure = null;
        var done = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
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
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = $"test:{name}",
        };
        thread.Start();
        if (!done.Wait(timeout))
        {
            return new Case(name, Outcome.Failed, clock.Elapsed,
                $"did not return within {timeout.TotalSeconds:F0}s — a hung case; the thread is "
                + "abandoned and the build continues");
        }
        clock.Stop();
        return failure is null
            ? new Case(name, Outcome.Passed, clock.Elapsed, null)
            : new Case(name, Outcome.Failed, clock.Elapsed, Innermost(failure));
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
