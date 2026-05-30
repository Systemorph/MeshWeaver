using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshWeaver.Reactive.Assertions;

/// <summary>
/// Configures a <c>BeEquivalentTo</c> comparison. FA-compatible surface: <see cref="Excluding{TProp}"/>,
/// <see cref="Including{TProp}"/>, <see cref="WithStrictOrdering"/>, plus the JSON-flavoured
/// <see cref="ExcludeTypeDiscriminator"/> / <see cref="ExcludeProperty{TDecl,TProp}"/> / <see cref="UsingJson"/>.
/// </summary>
public class EquivalencyOptions<T>
{
    internal readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase);
    internal HashSet<string>? Included;
    internal bool StrictOrdering;
    // FA's BeEquivalentTo is member-wise and type-agnostic, so by default we strip the polymorphic
    // "$type" discriminator before comparing (it's a serialization artifact, not a member). This makes
    // round-trip comparisons across different runtime types — e.g. MessageDelivery<Foo> vs the
    // deserialized MessageDelivery<RawJson> — behave like FA. Opt back in with IncludingTypeDiscriminator().
    internal bool StripTypeDiscriminator = true;
    internal readonly Dictionary<string, List<string>> ExcludedByDiscriminator = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Exclude a member from the comparison.</summary>
    public EquivalencyOptions<T> Excluding<TProp>(Expression<Func<T, TProp>> member)
    {
        Excluded.Add(EquivalencyEngine.MemberName(member));
        return this;
    }

    /// <summary>Compare ONLY the listed members (whitelist; top-level).</summary>
    public EquivalencyOptions<T> Including<TProp>(Expression<Func<T, TProp>> member)
    {
        (Included ??= new(StringComparer.OrdinalIgnoreCase)).Add(EquivalencyEngine.MemberName(member));
        return this;
    }

    /// <summary>Require arrays to match element-for-element in order (default is order-insensitive).</summary>
    public EquivalencyOptions<T> WithStrictOrdering()
    {
        StrictOrdering = true;
        return this;
    }

    /// <summary>Strip the <c>$type</c> polymorphism discriminator before comparing. On by default (FA-style); call with <c>false</c> to keep it.</summary>
    public EquivalencyOptions<T> ExcludeTypeDiscriminator(bool flag = true)
    {
        StripTypeDiscriminator = flag;
        return this;
    }

    /// <summary>Opt INTO comparing the <c>$type</c> polymorphism discriminator (stripped by default).</summary>
    public EquivalencyOptions<T> IncludingTypeDiscriminator()
    {
        StripTypeDiscriminator = false;
        return this;
    }

    /// <summary>Exclude a property on objects whose <c>$type</c> discriminator names <typeparamref name="TDecl"/>.</summary>
    public EquivalencyOptions<T> ExcludeProperty<TDecl, TProp>(Expression<Func<TDecl, TProp>> property)
    {
        var name = EquivalencyEngine.MemberName(property);
        foreach (var key in new[] { typeof(TDecl).Name, typeof(TDecl).FullName ?? typeof(TDecl).Name })
        {
            if (!ExcludedByDiscriminator.TryGetValue(key, out var list))
                list = ExcludedByDiscriminator[key] = [];
            list.Add(name);
        }
        return this;
    }

    /// <summary>Apply a JSON-flavoured configuration block. Retained for drop-in compatibility.</summary>
    public EquivalencyOptions<T> UsingJson(Func<EquivalencyOptions<T>, EquivalencyOptions<T>>? config = null)
        => config?.Invoke(this) ?? this;
}

/// <summary>
/// The deep-equality engine. Serializes both sides to a <see cref="JsonNode"/> tree with the caller-supplied
/// <see cref="JsonSerializerOptions"/> (which, for mesh objects, MUST come from the owning hub so the
/// <c>$type</c> discriminators line up) and compares structurally. <b>System.Text.Json only.</b>
/// </summary>
public static class EquivalencyEngine
{
    internal static string MemberName(LambdaExpression expr)
    {
        var body = expr.Body is UnaryExpression u ? u.Operand : expr.Body;
        if (body is MemberExpression m) return m.Member.Name;
        throw new ArgumentException("Expected a property or field access expression, e.g. x => x.Id.");
    }

    /// <summary>Normalizes a value to a <see cref="JsonNode"/> (JsonNode / RawJson pass through; everything else serializes).</summary>
    public static JsonNode? ToNode(object? value, JsonSerializerOptions options)
    {
        if (value is null) return null;
        if (value is JsonNode node) return node.DeepClone();
        var type = value.GetType();
        // RawJson (or any type exposing a string `Content` of raw JSON) — parse it, don't wrap it.
        if (type.Name == "RawJson" && type.GetProperty("Content")?.GetValue(value) is string content)
            return JsonNode.Parse(content);
        return JsonSerializer.SerializeToNode(value, type, options);
    }

    /// <summary>True when <paramref name="actual"/> is structurally equivalent to <paramref name="expected"/>.</summary>
    public static bool AreEquivalent<T>(object? actual, object? expected, JsonSerializerOptions options, EquivalencyOptions<T> opts)
    {
        var a = Prepare(ToNode(actual, options), opts, isRoot: true);
        var b = Prepare(ToNode(expected, options), opts, isRoot: true);
        return DeepEquals(a, b, opts.StrictOrdering);
    }

    private static JsonNode? Prepare<T>(JsonNode? node, EquivalencyOptions<T> opts, bool isRoot)
    {
        if (node is not JsonObject obj)
            return node;

        if (opts.StripTypeDiscriminator || opts.ExcludedByDiscriminator.Count > 0)
            foreach (var o in Descendants(obj).OfType<JsonObject>().ToList())
            {
                if (o["$type"]?.GetValue<string>() is { } discriminator
                    && opts.ExcludedByDiscriminator.TryGetValue(discriminator, out var props))
                    foreach (var p in props)
                        RemoveCaseInsensitive(o, p);
                if (opts.StripTypeDiscriminator)
                    o.Remove("$type");
            }

        if (isRoot && opts.Included is { Count: > 0 } incl)
            foreach (var key in obj.Select(kv => kv.Key).ToList())
                if (!incl.Contains(key))
                    obj.Remove(key);

        if (isRoot && opts.Excluded.Count > 0)
            foreach (var name in opts.Excluded)
                RemoveCaseInsensitive(obj, name);

        return node;
    }

    private static void RemoveCaseInsensitive(JsonObject obj, string name)
    {
        foreach (var key in obj.Select(kv => kv.Key).Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList())
            obj.Remove(key);
    }

    private static IEnumerable<JsonNode> Descendants(JsonNode? node)
    {
        if (node is null) yield break;
        yield return node;
        switch (node)
        {
            case JsonObject o:
                foreach (var kv in o)
                    foreach (var d in Descendants(kv.Value))
                        yield return d;
                break;
            case JsonArray a:
                foreach (var item in a)
                    foreach (var d in Descendants(item))
                        yield return d;
                break;
        }
    }

    private static bool DeepEquals(JsonNode? a, JsonNode? b, bool strictOrdering)
    {
        if (a is null || b is null) return a is null && b is null;

        if (a is JsonObject ao && b is JsonObject bo)
        {
            if (ao.Count != bo.Count) return false;
            foreach (var kv in ao)
                if (!bo.TryGetPropertyValue(kv.Key, out var bv) || !DeepEquals(kv.Value, bv, strictOrdering))
                    return false;
            return true;
        }

        if (a is JsonArray aa && b is JsonArray ba)
        {
            if (aa.Count != ba.Count) return false;
            if (strictOrdering)
            {
                for (var i = 0; i < aa.Count; i++)
                    if (!DeepEquals(aa[i], ba[i], strictOrdering))
                        return false;
                return true;
            }
            var remaining = ba.ToList();
            foreach (var ae in aa)
            {
                var idx = remaining.FindIndex(be => DeepEquals(ae, be, strictOrdering));
                if (idx < 0) return false;
                remaining.RemoveAt(idx);
            }
            return true;
        }

        return JsonNode.DeepEquals(a, b);
    }
}

/// <summary>
/// <c>BeEquivalentTo</c> — structural equivalence on any value or collection. The
/// <see cref="JsonSerializerOptions"/> is <b>required</b> and, for mesh objects, must come from the owning
/// hub (<c>Hub.JsonSerializerOptions</c>) so the polymorphic <c>$type</c> discriminators line up. A
/// no-options convenience overload may be added once the hub-routing is established everywhere.
/// </summary>
public static class EquivalencyAssertionExtensions
{
    public static AndConstraint<TAssertions> BeEquivalentTo<TSubject, TAssertions, TExpectation>(
        this ObjectAssertions<TSubject, TAssertions> assertions,
        TExpectation expectation,
        JsonSerializerOptions options,
        Func<EquivalencyOptions<TExpectation>, EquivalencyOptions<TExpectation>>? config = null,
        string because = "", params object[] becauseArgs)
        where TAssertions : ObjectAssertions<TSubject, TAssertions>
    {
        var opts = config is null ? new EquivalencyOptions<TExpectation>() : config(new EquivalencyOptions<TExpectation>());
        Az.Ensure(EquivalencyEngine.AreEquivalent(assertions.Subject, expectation, options, opts),
            () => $"Expected value to be equivalent to {Az.Fmt(EquivalencyEngine.ToNode(expectation, options))}{Az.Reason(because, becauseArgs)}, but found {Az.Fmt(EquivalencyEngine.ToNode(assertions.Subject, options))}.");
        return new AndConstraint<TAssertions>((TAssertions)assertions);
    }
}
