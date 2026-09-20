using System.Collections.Immutable;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MeshWeaver.Mesh;

/// <summary>
/// How one content member is combined with the value the OWNER currently holds, when a
/// <see cref="CreateOrUpdateNodeRequest"/> carries folds.
///
/// <para>Each rule names an operation, never a result — which is the whole point. A caller that
/// sends <c>accessCount: 6</c> has already decided the answer from a read that a second writer can
/// invalidate; a caller that sends <c>Sum 1</c> has not, so the arithmetic happens against the node
/// as it is actually held. See <c>Doc/Architecture/ExpressingAWrite</c>.</para>
/// </summary>
public enum FoldRule
{
    /// <summary>Numeric addition: the stored value plus the operand (absent stored value ⇒ 0).
    /// The counter bump.</summary>
    Sum,

    /// <summary>The larger of the stored value and the operand. Numbers, or ISO-8601 timestamps
    /// where BOTH sides parse as one. "Last seen at" written without reading first.</summary>
    Max,

    /// <summary>The smaller of the stored value and the operand. Same operand rules as
    /// <see cref="Max"/>.</summary>
    Min,

    /// <summary>Keep the stored value if the member is present on the live node; otherwise take the
    /// incoming one. "First seen at" — a create-once member that later writes must not move. Takes
    /// no operand.</summary>
    KeepExisting,
}

/// <summary>
/// One declarative fold over a TOP-LEVEL member of a node's content, carried on
/// <see cref="CreateOrUpdateNodeRequest.Folds"/> and applied by the owning side of the upsert.
///
/// <para><b>Member</b> is the member's SERIALIZED name (the JSON one — camelCase under the hub's
/// naming policy), matched case-insensitively for the same reason
/// <c>NodeTypeOperationalContent.MemberNames</c> is: writers that carry no naming policy fall back
/// to the PascalCase property name, and both spellings denote the same member. Build one with
/// <see cref="ContentFoldBuilder{T}"/> rather than typing the string, so a renamed property is a
/// compile error instead of a fold that silently matches nothing.</para>
///
/// <para>🚨 Deliberately TOP-LEVEL only. A nested pointer is expressible (<c>/a/b</c>) and is NOT
/// accepted: every fold the platform actually needs is a scalar member, and a nested one raises
/// questions this design has not answered (what a fold means when an intermediate object is absent,
/// and what it means for the RFC 7396 patch that carries the result). A leading <c>/</c> is
/// accepted and stripped so the wire form matches the <c>fold</c> op in the doc.</para>
/// </summary>
/// <param name="Member">The serialized member name, optionally written as <c>/name</c>.</param>
/// <param name="Rule">How the incoming value is combined with the stored one.</param>
public sealed record ContentFold(string Member, FoldRule Rule)
{
    /// <summary>
    /// The operand — what is added, or compared against. Required for
    /// <see cref="FoldRule.Sum"/>/<see cref="FoldRule.Max"/>/<see cref="FoldRule.Min"/>, and must be
    /// absent for <see cref="FoldRule.KeepExisting"/>, which takes none.
    ///
    /// <para>🚨 <see cref="JsonElement"/> and deliberately NOT <see cref="JsonNode"/>. A fold travels
    /// to the owning hub, and this messaging layer's <c>JsonNodeConverter</c> is <b>write-only</b> —
    /// its <c>Read</c> throws <see cref="NotImplementedException"/> unconditionally, so a
    /// <c>JsonNode</c> property serializes cleanly and then dies at the far end as a bare
    /// "Deserialization failed" naming neither the property nor the type. Measured, not assumed:
    /// that is how this first landed. <c>AFoldRoundTripsThroughTheHubSerializer</c> pins it.</para>
    /// </summary>
    public JsonElement? Operand { get; init; }

    /// <summary>The member name with any leading <c>/</c> removed.</summary>
    [JsonIgnore]
    public string MemberName => Member.StartsWith('/') ? Member[1..] : Member;
}

/// <summary>
/// Applies <see cref="ContentFold"/>s to an upsert's merged content against the node the owner
/// currently holds.
///
/// <para>🚨 <b>What this does and does not buy you.</b> It removes the caller's prior READ from the
/// write: <c>count + 1</c> is expressed without knowing <c>count</c>, so an upsert can create-or-fold
/// in one verb and a stale caller snapshot can no longer decide the value. It does NOT make the fold
/// atomic against a concurrent writer on ANOTHER mirror — the upsert's own write still travels as an
/// RFC 7396 merge patch carrying the resulting value, so two mirrors folding from the same base can
/// still lose an increment. Closing that needs the fold to survive the cross-hub hop, which is a
/// change to the sync protocol and is tracked separately. Do not read this type as making counters
/// cluster-safe; read it as making them caller-read-free.</para>
/// </summary>
public static class ContentFolds
{
    /// <summary>
    /// <paramref name="merged"/> with every fold applied against <paramref name="live"/>.
    ///
    /// <para>Returns <paramref name="merged"/> unchanged when there is nothing to do — no folds, or
    /// content that is not object-shaped (a fold addresses a member, and a non-object has none).
    /// A fold naming a member the incoming content does not carry still applies: that is the
    /// create-once and counter case, where the caller states the rule and not the member.</para>
    /// </summary>
    /// <param name="merged">The upsert's merged content, as produced by the full-instance merge.</param>
    /// <param name="live">The node as the owner currently holds it.</param>
    /// <param name="folds">The folds to apply, in order.</param>
    /// <param name="options">Serializer options, for turning typed content into JSON.</param>
    /// <returns>The folded content, or <paramref name="merged"/> when nothing applied.</returns>
    /// <exception cref="InvalidOperationException">A fold is not applicable to the values it found —
    /// a non-numeric <see cref="FoldRule.Sum"/>, or a <see cref="FoldRule.Max"/> over values that are
    /// neither both numbers nor both timestamps. Loud, because the alternative is a silently
    /// unapplied fold, which is the defect this whole design exists to remove.</exception>
    public static object? Apply(
        object? merged,
        MeshNode? live,
        IReadOnlyCollection<ContentFold>? folds,
        JsonSerializerOptions options)
    {
        if (folds is not { Count: > 0 })
            return merged;
        if (AsObject(merged, options) is not { } target)
            return merged;

        var liveContent = live is null ? null : AsObject(live.Content, options);
        var alreadyFolded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fold in folds)
        {
            var key = MatchingKey(target, fold.MemberName)
                      ?? (liveContent is not null ? MatchingKey(liveContent, fold.MemberName) : null)
                      ?? fold.MemberName;

            // 🚨 The BASE is the live value for the FIRST fold on a member, and the RUNNING RESULT
            // for any later one — so declared folds COMPOSE, which is what "applied in order" has
            // to mean. Reading `liveContent` every time instead makes two `Sum 1` produce
            // `live + 1` rather than `live + 2`: the second fold silently overwrites the first, and
            // the caller sees a plausible number that is simply wrong.
            //
            // It must never fall back to the INCOMING value for the first fold. That value is the
            // caller's un-read guess, and preferring it is exactly the staleness a fold exists to
            // remove.
            var baseValue = alreadyFolded.Contains(fold.MemberName)
                ? target.TryGetPropertyValue(key, out var running) ? running : null
                : liveContent is null ? null : Lookup(liveContent, fold.MemberName);

            target[key] = Combine(
                fold, baseValue, target.TryGetPropertyValue(key, out var incoming) ? incoming : null);
            alreadyFolded.Add(fold.MemberName);
        }

        return target;
    }

    /// <summary>The folded value for one member.</summary>
    private static JsonNode? Combine(ContentFold fold, JsonNode? stored, JsonNode? incoming) =>
        fold.Rule switch
        {
            FoldRule.KeepExisting => stored?.DeepClone() ?? incoming?.DeepClone(),
            FoldRule.Sum => JsonValue.Create(
                AsNumber(stored, fold, "the stored value") + OperandNumber(fold)),
            FoldRule.Max or FoldRule.Min => Extremum(fold, stored),
            _ => incoming?.DeepClone(),
        };

    /// <summary>
    /// <see cref="FoldRule.Max"/>/<see cref="FoldRule.Min"/> over numbers, or over ISO-8601
    /// timestamps when both sides are one. An absent stored value yields the operand.
    /// </summary>
    private static JsonNode? Extremum(ContentFold fold, JsonNode? stored)
    {
        if (fold.Operand is not { } operandElement)
            throw Inapplicable(fold, $"{fold.Rule} needs an operand and none was supplied");
        var operand = ToNode(operandElement);
        if (stored is null)
            return operand;

        var wantLarger = fold.Rule == FoldRule.Max;
        if (TryTimestamp(stored, out var storedAt) && TryTimestampElement(operandElement, out var operandAt))
            return (wantLarger ? operandAt > storedAt : operandAt < storedAt) ? operand : stored.DeepClone();

        var storedNumber = AsNumber(stored, fold, "the stored value");
        var operandNumber = OperandNumber(fold);
        return (wantLarger ? operandNumber > storedNumber : operandNumber < storedNumber)
            ? operand
            : stored.DeepClone();
    }

    /// <summary>The operand as a number, or a loud refusal.</summary>
    private static decimal OperandNumber(ContentFold fold)
    {
        if (fold.Operand is not { } element)
            throw Inapplicable(fold, $"{fold.Rule} needs an operand and none was supplied");
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var d))
            return d;
        if (element.ValueKind == JsonValueKind.String
            && decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        throw Inapplicable(fold, $"the operand is {element.ValueKind}, which {fold.Rule} cannot combine");
    }

    /// <summary>The operand element as a mutable node.</summary>
    private static JsonNode? ToNode(JsonElement element) => JsonNode.Parse(element.GetRawText());

    /// <summary>Whether the element is an ISO-8601 timestamp.</summary>
    private static bool TryTimestampElement(JsonElement element, out DateTimeOffset value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.String
               && DateTimeOffset.TryParse(
                   element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
    }

    /// <summary>The node as a number, or a loud refusal naming which side was not one.</summary>
    private static decimal AsNumber(JsonNode? node, ContentFold fold, string which)
    {
        if (node is null) return 0m;
        if (node is JsonValue value)
        {
            // 🚨 A JsonValue is backed by a CLR type OR by a JsonElement, and `TryGetValue<decimal>`
            // does NOT bridge between them: a JsonValue<int> (what an object-initializer literal
            // produces) answers FALSE for decimal, while a parsed one (JsonElement-backed, which is
            // what content read back from the store is) answers true. Asking only for decimal
            // therefore worked end-to-end and refused the in-memory case — a split this suite only
            // caught because a pure test built its live node from a literal.
            if (value.TryGetValue<decimal>(out var dec)) return dec;
            if (value.TryGetValue<long>(out var l)) return l;
            if (value.TryGetValue<int>(out var i)) return i;
            if (value.TryGetValue<double>(out var dbl)) return (decimal)dbl;
            if (value.TryGetValue<JsonElement>(out var element))
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var ed))
                    return ed;
                if (element.ValueKind == JsonValueKind.String
                    && decimal.TryParse(
                        element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var es))
                    return es;
            }
            if (value.TryGetValue<string>(out var s)
                && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        throw Inapplicable(fold, $"{which} is {Describe(node)}, which {fold.Rule} cannot combine");
    }

    /// <summary>Whether the node is an ISO-8601 timestamp.</summary>
    private static bool TryTimestamp(JsonNode? node, out DateTimeOffset value)
    {
        value = default;
        return node is JsonValue v
               && v.TryGetValue<string>(out var text)
               && DateTimeOffset.TryParse(
                   text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
    }

    private static InvalidOperationException Inapplicable(ContentFold fold, string because) =>
        new($"Fold {fold.Rule} on content member '{fold.MemberName}' cannot be applied: {because}. "
            + "A fold states an operation over a value the owner holds, so it refuses rather than "
            + "writing a value nobody asked for.");

    private static string Describe(JsonNode? node) =>
        node switch
        {
            null => "absent",
            JsonArray => "an array",
            JsonObject => "an object",
            JsonValue v when v.TryGetValue<string>(out var s) => $"the string '{s}'",
            JsonValue v when v.TryGetValue<bool>(out var b) => $"the boolean {b}",
            _ => "not a number",
        };

    /// <summary>The object's key for <paramref name="member"/>, matched case-insensitively.</summary>
    private static string? MatchingKey(JsonObject content, string member) =>
        content.FirstOrDefault(m => string.Equals(m.Key, member, StringComparison.OrdinalIgnoreCase)).Key;

    /// <summary>The member's value, matched case-insensitively.</summary>
    private static JsonNode? Lookup(JsonObject content, string member) =>
        MatchingKey(content, member) is { } key ? content[key] : null;

    /// <summary>
    /// Content as a fresh, mutable <see cref="JsonObject"/>, or null when it is not object-shaped.
    /// Same three-shape handling — and the same reason for the CONCRETE-type serialize rather than
    /// the <c>object</c> overload — as <c>NodeTypeOperationalContent.ContentObject</c>.
    /// </summary>
    private static JsonObject? AsObject(object? content, JsonSerializerOptions options) =>
        content switch
        {
            null => null,
            JsonObject jo => (JsonObject)jo.DeepClone(),
            JsonNode => null,
            JsonElement je => je.ValueKind == JsonValueKind.Object ? JsonObject.Create(je) : null,
            var typed => JsonSerializer.SerializeToNode(typed, typed.GetType(), options) as JsonObject,
        };
}

/// <summary>
/// Builds <see cref="ContentFold"/>s from property expressions, so the member name comes from the
/// compiler rather than from a string literal.
///
/// <para>This is pattern 1 of <c>Doc/Architecture/ExpressingAWrite</c> in its fluent form: the author
/// writes what the change MEANS, and it lowers to the wire ops of pattern 2. The expression is a
/// plain member access — deliberately not a general lambda — because that is the whole of what a
/// fold addresses, and it is analysable without an expression interpreter.</para>
///
/// <code>
/// request.WithFolds&lt;UserActivityRecord&gt;(f => f
///     .Sum(r => r.AccessCount, 1)
///     .KeepExisting(r => r.FirstAccessedAt)
///     .Max(r => r.LastAccessedAt, now));
/// </code>
/// </summary>
/// <typeparam name="T">The content type whose members are being folded.</typeparam>
public sealed class ContentFoldBuilder<T>
{
    private readonly JsonSerializerOptions options;
    private ImmutableList<ContentFold> folds = ImmutableList<ContentFold>.Empty;

    /// <summary>Creates a builder that names members under <paramref name="options"/>' policy.</summary>
    /// <param name="options">Serializer options, for the property naming policy.</param>
    public ContentFoldBuilder(JsonSerializerOptions options) => this.options = options;

    /// <summary>The folds built so far, in the order they were declared.</summary>
    public ImmutableList<ContentFold> Folds => folds;

    /// <summary>Adds the stored value and <paramref name="operand"/> — the counter bump.</summary>
    /// <param name="member">The member to fold.</param>
    /// <param name="operand">What to add.</param>
    /// <returns>This builder.</returns>
    public ContentFoldBuilder<T> Sum<TMember>(Expression<Func<T, TMember>> member, TMember operand) =>
        Add(member, FoldRule.Sum, operand);

    /// <summary>Takes the larger of the stored value and <paramref name="operand"/>.</summary>
    /// <param name="member">The member to fold.</param>
    /// <param name="operand">The candidate value.</param>
    /// <returns>This builder.</returns>
    public ContentFoldBuilder<T> Max<TMember>(Expression<Func<T, TMember>> member, TMember operand) =>
        Add(member, FoldRule.Max, operand);

    /// <summary>Takes the smaller of the stored value and <paramref name="operand"/>.</summary>
    /// <param name="member">The member to fold.</param>
    /// <param name="operand">The candidate value.</param>
    /// <returns>This builder.</returns>
    public ContentFoldBuilder<T> Min<TMember>(Expression<Func<T, TMember>> member, TMember operand) =>
        Add(member, FoldRule.Min, operand);

    /// <summary>Keeps the stored value when the live node carries this member — create-once.</summary>
    /// <param name="member">The member to preserve.</param>
    /// <returns>This builder.</returns>
    public ContentFoldBuilder<T> KeepExisting<TMember>(Expression<Func<T, TMember>> member) =>
        Add<TMember>(member, FoldRule.KeepExisting, default, withOperand: false);

    private ContentFoldBuilder<T> Add<TMember>(
        Expression<Func<T, TMember>> member, FoldRule rule, TMember? operand, bool withOperand = true)
    {
        var fold = new ContentFold(SerializedName(member), rule)
        {
            Operand = withOperand ? JsonSerializer.SerializeToElement(operand, options) : null,
        };
        folds = folds.Add(fold);
        return this;
    }

    /// <summary>
    /// The serialized name of the property a member-access expression names — honouring
    /// <see cref="JsonPropertyNameAttribute"/> first, then the options' naming policy, then the
    /// declared name. Refuses anything that is not a direct property access, because a fold has no
    /// meaning for a computed expression.
    /// </summary>
    private string SerializedName<TMember>(Expression<Func<T, TMember>> member)
    {
        var body = member.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert
            ? convert.Operand
            : member.Body;
        if (body is not MemberExpression { Member: PropertyInfo property }
            || property.DeclaringType?.IsAssignableFrom(typeof(T)) is false)
            throw new ArgumentException(
                $"A fold names a property of {typeof(T).Name} directly (r => r.AccessCount). "
                + $"'{member.Body}' is not a property access, and a fold over a computed expression "
                + "has no meaning — the owner applies a rule to a member, not an expression.",
                nameof(member));

        return property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
               ?? options.PropertyNamingPolicy?.ConvertName(property.Name)
               ?? property.Name;
    }
}

/// <summary>Fluent construction of <see cref="CreateOrUpdateNodeRequest.Folds"/>.</summary>
public static class ContentFoldExtensions
{
    /// <summary>
    /// Returns a copy of <paramref name="request"/> carrying the folds <paramref name="build"/>
    /// declares over <typeparamref name="T"/>'s members.
    ///
    /// <code>
    /// new CreateOrUpdateNodeRequest(seed)
    ///     .WithFolds&lt;UserActivityRecord&gt;(hub.JsonSerializerOptions, f => f
    ///         .Sum(r => r.AccessCount, 1)
    ///         .KeepExisting(r => r.FirstAccessedAt));
    /// </code>
    /// </summary>
    /// <typeparam name="T">The content type being folded.</typeparam>
    /// <param name="request">The upsert to add folds to.</param>
    /// <param name="options">Serializer options — supplies the property naming policy.</param>
    /// <param name="build">Declares the folds.</param>
    /// <returns>A copy carrying the declared folds, appended to any already present.</returns>
    public static CreateOrUpdateNodeRequest WithFolds<T>(
        this CreateOrUpdateNodeRequest request,
        JsonSerializerOptions options,
        Func<ContentFoldBuilder<T>, ContentFoldBuilder<T>> build)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(build);
        var declared = build(new ContentFoldBuilder<T>(options)).Folds;
        return request with
        {
            Folds = request.Folds is { Count: > 0 } existing
                ? [.. existing, .. declared]
                : declared,
        };
    }
}
