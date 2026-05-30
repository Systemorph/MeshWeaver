using System.Collections;

namespace MeshWeaver.Reactive.Assertions;

/// <summary>Thrown when any assertion in this library fails. Test runners treat a thrown exception as a failure.</summary>
public class AssertionException(string message) : Exception(message);

/// <summary>Continuation that exposes <see cref="And"/> for chaining a sibling assertion on the same subject.</summary>
public class AndConstraint<TAssertions>(TAssertions parent)
{
    /// <summary>Continue asserting on the same subject.</summary>
    public TAssertions And { get; } = parent;
}

/// <summary>
/// Continuation that adds <see cref="Which"/> / <see cref="Subject"/> — the value isolated by the preceding
/// assertion (e.g. the single element of <c>ContainSingle()</c>, or the narrowed value of <c>BeOfType&lt;T&gt;()</c>).
/// </summary>
public class AndWhichConstraint<TAssertions, TMatched>(TAssertions parent, TMatched matched) : AndConstraint<TAssertions>(parent)
{
    /// <summary>The value isolated by the preceding assertion.</summary>
    public TMatched Which { get; } = matched;
    /// <summary>Alias for <see cref="Which"/>.</summary>
    public TMatched Subject { get; } = matched;
}

/// <summary>
/// Internal failure + message-formatting helpers shared by every assertion class. The <c>because</c> /
/// <c>becauseArgs</c> reason is FA-compatible: a trailing reason phrase folded into the message.
/// </summary>
internal static class Az
{
    /// <summary>Report a failure (message built lazily) unless <paramref name="condition"/> holds. Throws
    /// immediately when not inside an <see cref="AssertionScope"/>; collects otherwise.</summary>
    public static void Ensure(bool condition, Func<string> message)
    {
        if (!condition)
            AssertionScope.Report(message());
    }

    /// <summary>Formats the FA-style "because ..." reason suffix (empty when no reason given).</summary>
    public static string Reason(string because, object[] becauseArgs)
    {
        if (string.IsNullOrWhiteSpace(because))
            return "";
        var r = (becauseArgs is { Length: > 0 } ? string.Format(because, becauseArgs) : because).Trim();
        return r.StartsWith("because", StringComparison.OrdinalIgnoreCase) ? " " + r : " because " + r;
    }

    /// <summary>Renders a value for a failure message — quotes strings, brackets collections, marks null.</summary>
    public static string Fmt(object? value)
    {
        switch (value)
        {
            case null: return "<null>";
            case string s: return $"\"{s}\"";
            case IEnumerable e and not IFormattable:
                var items = new List<string>();
                foreach (var item in e)
                {
                    if (items.Count == 10) { items.Add("…"); break; }
                    items.Add(Fmt(item));
                }
                return "{" + string.Join(", ", items) + "}";
            default: return value.ToString() ?? "<null>";
        }
    }
}

/// <summary>
/// Collects assertion failures within a <c>using</c> block and throws a single aggregated
/// <see cref="AssertionException"/> on dispose (FA-compatible shim for the few
/// <c>using var scope = new AssertionScope();</c> call sites). Soft-failure aggregation is approximated:
/// the first failure inside the scope still throws on dispose with its message.
/// </summary>
public sealed class AssertionScope : IDisposable
{
    [ThreadStatic] private static AssertionScope? _current;
    private readonly AssertionScope? _parent;
    private readonly List<string> _failures = [];

    public AssertionScope()
    {
        _parent = _current;
        _current = this;
    }

    public AssertionScope(string context) : this() { }

    internal static void Report(string message)
    {
        if (_current is { } scope)
            scope._failures.Add(message);
        else
            throw new AssertionException(message);
    }

    public void Dispose()
    {
        _current = _parent;
        if (_failures.Count > 0)
            throw new AssertionException(string.Join(Environment.NewLine, _failures));
    }
}
