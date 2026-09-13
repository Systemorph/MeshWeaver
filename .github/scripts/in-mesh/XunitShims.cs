// <meshweaver>
// Id: XunitShims
// DisplayName: xunit's Assert vocabulary for migrated in-mesh tests — no xunit
// </meshweaver>
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

/// <summary>
/// The xunit <c>Assert</c> surface the migrated suites call (4,800 call sites on the first CI bake of
/// core #4185, 2026-09-13), as plain exceptions — so a converted file compiles unchanged. A failure throws
/// <see cref="MeshWeaver.Reactive.Assertions.AssertionException"/>, which the runner reports with its message.
/// </summary>
public static class Assert
{
    private static Exception Fail(string message) => new MeshWeaver.Reactive.Assertions.AssertionException(message);
    public static void True(bool condition, string? message = null) { if (!condition) throw Fail(message ?? "Assert.True failed"); }
    public static void True(bool? condition, string? message = null) => True(condition == true, message);
    public static void False(bool condition, string? message = null) { if (condition) throw Fail(message ?? "Assert.False failed"); }
    public static void False(bool? condition, string? message = null) => False(condition == true, message);
    public static void Null(object? value) { if (value is not null) throw Fail($"Assert.Null failed — got {value}"); }
    public static void NotNull(object? value) { if (value is null) throw Fail("Assert.NotNull failed"); }
    public static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw Fail($"Assert.Equal failed — expected {Show(expected)}, got {Show(actual)}"); }
    public static void Equal<T>(IEnumerable<T> expected, IEnumerable<T> actual) { if (!expected.SequenceEqual(actual)) throw Fail($"Assert.Equal failed — sequences differ: expected [{string.Join(", ", expected.Select(v => Show(v)))}], got [{string.Join(", ", actual.Select(v => Show(v)))}]"); }
    public static void Equal(string expected, string actual, bool ignoreCase) { if (!string.Equals(expected, actual, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw Fail($"Assert.Equal failed — expected '{expected}', got '{actual}'"); }
    public static void Equal(double expected, double actual, int precision) { if (Math.Round(expected, precision) != Math.Round(actual, precision)) throw Fail($"Assert.Equal failed — expected {expected}, got {actual} (precision {precision})"); }
    public static void NotEqual<T>(T expected, T actual) { if (EqualityComparer<T>.Default.Equals(expected, actual)) throw Fail($"Assert.NotEqual failed — both {Show(actual)}"); }
    public static void Same(object? expected, object? actual) { if (!ReferenceEquals(expected, actual)) throw Fail("Assert.Same failed"); }
    public static void NotSame(object? expected, object? actual) { if (ReferenceEquals(expected, actual)) throw Fail("Assert.NotSame failed"); }
    public static void Contains(string expected, string? actual) { if (actual is null || !actual.Contains(expected, StringComparison.Ordinal)) throw Fail($"Assert.Contains failed — '{expected}' not in '{actual}'"); }
    public static void Contains(string expected, string? actual, StringComparison comparison) { if (actual is null || !actual.Contains(expected, comparison)) throw Fail($"Assert.Contains failed — '{expected}' not in '{actual}'"); }
    public static void Contains<T>(T expected, IEnumerable<T> collection) { if (!collection.Contains(expected)) throw Fail($"Assert.Contains failed — {Show(expected)} not in the collection"); }
    public static void Contains<T>(IEnumerable<T> collection, Func<T, bool> filter) { if (!collection.Any(filter)) throw Fail("Assert.Contains failed — no element matches"); }
    public static void Contains<TKey, TValue>(TKey key, IDictionary<TKey, TValue> dict) { if (!dict.ContainsKey(key)) throw Fail($"Assert.Contains failed — key {Show(key)} absent"); }
    public static void DoesNotContain(string expected, string? actual) { if (actual is not null && actual.Contains(expected, StringComparison.Ordinal)) throw Fail($"Assert.DoesNotContain failed — '{expected}' in '{actual}'"); }
    public static void DoesNotContain<T>(T expected, IEnumerable<T> collection) { if (collection.Contains(expected)) throw Fail($"Assert.DoesNotContain failed — {Show(expected)} present"); }
    public static void DoesNotContain<T>(IEnumerable<T> collection, Func<T, bool> filter) { if (collection.Any(filter)) throw Fail("Assert.DoesNotContain failed — an element matches"); }
    public static void Empty(System.Collections.IEnumerable collection) { if (collection.Cast<object?>().Any()) throw Fail("Assert.Empty failed — the collection has elements"); }
    public static void NotEmpty(System.Collections.IEnumerable collection) { if (!collection.Cast<object?>().Any()) throw Fail("Assert.NotEmpty failed — the collection is empty"); }
    public static T Single<T>(IEnumerable<T> collection) { var list = collection.ToList(); if (list.Count != 1) throw Fail($"Assert.Single failed — {list.Count} element(s)"); return list[0]; }
    public static T Single<T>(IEnumerable<T> collection, Func<T, bool> predicate) { var list = collection.Where(predicate).ToList(); if (list.Count != 1) throw Fail($"Assert.Single failed — {list.Count} element(s) match"); return list[0]; }
    public static void All<T>(IEnumerable<T> collection, Action<T> action) { foreach (var item in collection) action(item); }
    public static void StartsWith(string expected, string? actual) { if (actual is null || !actual.StartsWith(expected, StringComparison.Ordinal)) throw Fail($"Assert.StartsWith failed — '{actual}' does not start with '{expected}'"); }
    public static void EndsWith(string expected, string? actual) { if (actual is null || !actual.EndsWith(expected, StringComparison.Ordinal)) throw Fail($"Assert.EndsWith failed — '{actual}' does not end with '{expected}'"); }
    public static void Matches(string pattern, string? actual) { if (actual is null || !System.Text.RegularExpressions.Regex.IsMatch(actual, pattern)) throw Fail($"Assert.Matches failed — '{actual}' !~ /{pattern}/"); }
    public static void InRange<T>(T actual, T low, T high) where T : IComparable<T> { if (actual.CompareTo(low) < 0 || actual.CompareTo(high) > 0) throw Fail($"Assert.InRange failed — {Show(actual)} not in [{Show(low)}, {Show(high)}]"); }
    public static T IsType<T>(object? value) { if (value is not T t) throw Fail($"Assert.IsType failed — expected {typeof(T).Name}, got {value?.GetType().Name ?? "null"}"); return t; }
    public static T IsAssignableFrom<T>(object? value) { if (value is not T t) throw Fail($"Assert.IsAssignableFrom failed — expected {typeof(T).Name}, got {value?.GetType().Name ?? "null"}"); return t; }
    public static void IsNotType<T>(object? value) { if (value is T) throw Fail($"Assert.IsNotType failed — is {typeof(T).Name}"); }
    public static void Fail(string message, params object?[] _) => throw Fail(message);
    public static T Throws<T>(Action action) where T : Exception { try { action(); } catch (T ex) { return ex; } catch (Exception ex) { throw Fail($"Assert.Throws failed — expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}"); } throw Fail($"Assert.Throws failed — expected {typeof(T).Name}, nothing thrown"); }
    public static T Throws<T>(Func<object?> action) where T : Exception => Throws<T>(() => { action(); });
    public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T ex) { return ex; } catch (Exception ex) { throw Fail($"Assert.ThrowsAsync failed — expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}"); } throw Fail($"Assert.ThrowsAsync failed — expected {typeof(T).Name}, nothing thrown"); }
    public static T ThrowsAny<T>(Action action) where T : Exception { try { action(); } catch (T ex) { return ex; } throw Fail($"Assert.ThrowsAny failed — expected {typeof(T).Name}"); }
    public static async Task<T> ThrowsAnyAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T ex) { return ex; } throw Fail($"Assert.ThrowsAnyAsync failed — expected {typeof(T).Name}"); }
    public static void DoesNotContain(string expected, string? actual, StringComparison comparison) { if (actual is not null && actual.Contains(expected, comparison)) throw Fail($"Assert.DoesNotContain failed — '{expected}' in '{actual}'"); }
    public static void Equal<T>(T expected, T actual, IEqualityComparer<T> comparer) { if (!comparer.Equals(expected, actual)) throw Fail($"Assert.Equal failed — expected {Show(expected)}, got {Show(actual)}"); }
    public static void Equal(decimal expected, decimal actual, int precision) { if (Math.Round(expected, precision) != Math.Round(actual, precision)) throw Fail($"Assert.Equal failed — expected {expected}, got {actual} (precision {precision})"); }
    public static void Equal(DateTime expected, DateTime actual, TimeSpan precision) { if ((expected - actual).Duration() > precision) throw Fail($"Assert.Equal failed — {expected:o} vs {actual:o} beyond {precision}"); }
    public static void NotEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual) { if (expected.SequenceEqual(actual)) throw Fail("Assert.NotEqual failed — sequences are equal"); }
    public static void Equivalent(object? expected, object? actual) { if (!Equals(expected, actual) && System.Text.Json.JsonSerializer.Serialize(expected) != System.Text.Json.JsonSerializer.Serialize(actual)) throw Fail($"Assert.Equivalent failed — expected {Show(expected)}, got {Show(actual)}"); }
    public static void Collection<T>(IEnumerable<T> collection, params Action<T>[] inspectors) { var list = collection.ToList(); if (list.Count != inspectors.Length) throw Fail($"Assert.Collection failed — {list.Count} element(s), {inspectors.Length} inspector(s)"); for (var i = 0; i < list.Count; i++) inspectors[i](list[i]); }
    public static void Distinct<T>(IEnumerable<T> collection) { var list = collection.ToList(); if (list.Distinct().Count() != list.Count) throw Fail("Assert.Distinct failed — duplicates present"); }
    public static void Subset<T>(ISet<T> expectedSuperset, ISet<T>? actual) { if (actual is null || !actual.IsSubsetOf(expectedSuperset)) throw Fail("Assert.Subset failed"); }
    public static void Superset<T>(ISet<T> expectedSubset, ISet<T>? actual) { if (actual is null || !actual.IsSupersetOf(expectedSubset)) throw Fail("Assert.Superset failed"); }
    public static void Multiple(params Action[] checks) { var failures = new List<string>(); foreach (var c in checks) { try { c(); } catch (Exception ex) { failures.Add(ex.Message); } } if (failures.Count > 0) throw Fail("Assert.Multiple failed:\n" + string.Join("\n", failures)); }
    public static void Skip(string reason) => throw Fail("skipped: " + reason);
    public static void SkipWhen(bool condition, string reason) { if (condition) throw Fail("skipped: " + reason); }
    public static void SkipUnless(bool condition, string reason) { if (!condition) throw Fail("skipped: " + reason); }
    public static void ProperSubset<T>(ISet<T> expectedSuperset, ISet<T>? actual) { if (actual is null || !actual.IsProperSubsetOf(expectedSuperset)) throw Fail("Assert.ProperSubset failed"); }
    public static void Raises<T>(Action<Action<T>> attach, Action<Action<T>> detach, Action testCode) { var raised = false; Action<T> h = _ => raised = true; attach(h); try { testCode(); } finally { detach(h); } if (!raised) throw Fail("Assert.Raises failed — no event"); }
    private static string Show(object? v) => v is null ? "null" : v is string s ? $"'{s}'" : v.ToString() ?? "?";
}

/// <summary>xunit's <c>Record</c>: capture the exception a delegate throws, or null.</summary>
public static class Record
{
    public static Exception? Exception(Action testCode) { try { testCode(); return null; } catch (Exception ex) { return ex; } }
    public static Exception? Exception(Func<object?> testCode) { try { testCode(); return null; } catch (Exception ex) { return ex; } }
    public static async Task<Exception?> ExceptionAsync(Func<Task> testCode) { try { await testCode(); return null; } catch (Exception ex) { return ex; } }
}

/// <summary>
/// The fixture's <c>TestTimeouts</c> (test/MeshWeaver.Fixture/TestTimeouts.cs), copied verbatim minus its
/// namespace: an in-mesh suite has no reference to the xunit fixture assembly, and the bounds it derives
/// from the platform (<see cref="MeshWeaver.Mesh.LatePatchResponseRegistry.WriteVerdictBound"/>) are
/// the SAME numbers the xunit estate waits with. Regenerated by generate-in-mesh-suites.py.
/// </summary>


/// <summary>
/// The one place a test waiting time is decided.
///
/// <para>🚨 <b>A convergence has no deadline of its own.</b> A reconnect-and-drain, a projection
/// settling, a cancellation restarting pending rounds — none of these promises to finish in N
/// seconds. Every bound on one is a guess about how fast the machine is, and a guess written as a
/// literal is a guess that cannot be revisited.</para>
///
/// <para>The literal that was written is <c>30 s</c>, roughly 2,679 times across the two
/// repositories. On 2026-08-29 six failures landed at 30–33 s in a single evening, in different
/// tests, different suites and different repos — because the same guess had been copied everywhere
/// and CI is slower than the laptop it was made on. There is a documented ~1.7× CI/local ratio,
/// so a 30 s local bound leaves about 18 s of CI headroom, and under runner contention that is
/// gone.</para>
///
/// <para>🚨 <b>The inner bound must be strictly less than the outer one, and that is why both live
/// here.</b> 24 files carry <c>[Fact(Timeout = 30000)]</c> AND a 30 s internal wait: xunit kills
/// the test at the exact moment the wait would have expired, so the assertion never fires and the
/// failure is reported as an anonymous timeout instead of naming what did not converge. Scaling
/// only the inner bound changes nothing in precisely the files that need it. Deriving both from
/// one factor keeps the ordering by construction rather than by whoever writes the next test
/// remembering it.</para>
/// </summary>
public static class TestTimeouts
{
    /// <summary>
    /// 🚨 The framework's own outer bound on a caller-visible write — the instant
    /// <c>UpdateRemote</c> gives up and reports <c>OwnerUnreachable</c>. A test wait must DOMINATE
    /// it, never equal it.
    ///
    /// <para>The old baseline was a literal <c>30 s</c>, which is exactly
    /// <c>LateResponseWatchBound</c> and one second below this. A test awaiting a mesh write
    /// therefore gave up one second BEFORE the framework produced its diagnosis, every single time
    /// — so the failure always read "the observable emitted nothing at all" and never
    /// "OwnerUnreachable: the owner produced no terminal for this patch". The bound was placed at
    /// precisely the value that destroys the most information (#2819).</para>
    /// </summary>
    private static TimeSpan FrameworkWriteBound => MeshWeaver.Mesh.LatePatchResponseRegistry.WriteVerdictBound;

    /// <summary>
    /// Local baseline for one convergence wait. Every other value is derived from it — and it is
    /// itself derived from <see cref="FrameworkWriteBound"/> rather than written as a literal, so
    /// the ordering holds by construction instead of by whoever writes the next test remembering
    /// it.
    ///
    /// <para>The slack is ADDITIVE, not a ratio: what has to be covered is the propagation of one
    /// terminal — the framework produces <c>OwnerUnreachable</c> at the write bound and it has to
    /// reach the assertion — and that cost does not scale with the bound. Five seconds is ample
    /// for it and keeps the local wait near the familiar half-minute; a multiplier would inflate
    /// every wedged test's failure time to buy the same few seconds.</para>
    /// </summary>
    private static TimeSpan LocalConvergence => FrameworkWriteBound + TimeSpan.FromSeconds(5);

    /// <summary>
    /// How much slower CI is assumed to be. Overridable with <c>MW_TEST_TIMEOUT_FACTOR</c> so the
    /// number can be tuned against evidence — a shared bound is only an improvement on a literal
    /// if it can actually be changed in one place.
    /// </summary>
    private const double DefaultCiFactor = 3.0;

    /// <summary>
    /// True on a CI runner. Both variables are checked: <c>CI</c> is the convention, and
    /// <c>GITHUB_ACTIONS</c> is what this fleet's runners actually set.
    /// </summary>
    public static bool IsContinuousIntegration =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

    private static double Factor
    {
        get
        {
            if (!IsContinuousIntegration)
                return 1.0;
            var raw = Environment.GetEnvironmentVariable("MW_TEST_TIMEOUT_FACTOR");
            // A malformed override must not silently become 1.0 — that would quietly restore the
            // very bound this type exists to widen. Fall back to the default instead.
            return double.TryParse(raw, out var parsed) && parsed > 0 ? parsed : DefaultCiFactor;
        }
    }

    /// <summary>
    /// How long to wait for a convergence that has no deadline of its own. Use this instead of
    /// writing a literal.
    /// </summary>
    public static TimeSpan Convergence => LocalConvergence * Factor;

    /// <summary>
    /// The value for <c>[Fact(Timeout = …)]</c>, in milliseconds — deliberately LARGER than
    /// <see cref="Convergence"/> so an inner wait can lose first and report what it was waiting
    /// for. A test whose xunit timeout equals its internal wait can only ever fail anonymously.
    /// </summary>
    public static int TestMilliseconds => (int)(Convergence * OuterMargin).TotalMilliseconds;

    /// <summary>
    /// The gap between the inner and outer bound. 2× is deliberate rather than tight: the outer
    /// bound exists to stop a WEDGE, not to police a slow convergence, so it should be nowhere
    /// near the inner one.
    /// </summary>
    private const double OuterMargin = 2.0;

    /// <summary>A convergence expected to be quick — a local projection, a cached read.</summary>
    public static TimeSpan Quick => Convergence / 3;

    /// <summary>
    /// A convergence crossing a silo or a real network hop, where the platform's own request
    /// timeout (60 s) is the thing being waited on rather than a local settle.
    /// </summary>
    public static TimeSpan CrossSilo => Convergence * 2;
}
