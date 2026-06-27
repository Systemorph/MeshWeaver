using System.Collections.Immutable;
using System.Text.Json;
using Json.Pointer;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

/// <summary>
/// Provides methods and constants for working with layout area references.
/// </summary>
public record LayoutAreaReference : WorkspaceReference<EntityStore>
{
    /// <summary>
    /// Creates a reference to the named layout area.
    /// </summary>
    /// <param name="area">The name of the layout area, or null.</param>
    public LayoutAreaReference(string? area)
    {
        Area = area;
        parameters = new(ParseParameters);
    }

    private IReadOnlyDictionary<string, string?> ParseParameters()
    {
        if (Id is null)
            return ImmutableDictionary<string, string?>.Empty;

        var parts = Id.ToString()!.Split('?').Skip(1).SelectMany(x => x.Split('&', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        if (parts.Length == 0)
            return ImmutableDictionary<string, string?>.Empty;

        return parts.Select(p =>
            {
                var split = p.Split('=');
                if (split.Length == 1)
                    return new KeyValuePair<string, string?>(Uri.UnescapeDataString(split[0]), null);
                if (split.Length == 2)
                    return new KeyValuePair<string, string?>(Uri.UnescapeDataString(split[0]),
                        Uri.UnescapeDataString(split[1]));
                throw new InvalidOperationException($"Invalid parameter format: {p}");
            })
            .ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Name of the layout area.
    /// </summary>
    public string? Area { get; init; }
    /// <summary>
    /// Id of the layout area. Can contain optional parameters after a ? in URL format.
    /// </summary>
    public object? Id { get; init; }

    /// <summary>
    /// Can specify a separate layout.
    /// </summary>
    public string? Layout { get; init; }

    /// <summary>
    /// Constant for the data area.
    /// </summary>
    public const string Data = "data";

    /// <summary>
    /// The constant string for areas.
    /// </summary>
    public const string Areas = "areas";

    /// <summary>
    /// Reserved DataContext prefix marking a <b>node-bound</b> DataContext: the control's field
    /// pointers resolve against a live <c>MeshNode</c> (via <c>IMeshNodeStreamCache</c>),
    /// NOT against a layout-area <c>/data/{id}</c> replica. This is the encoding that lets the
    /// EXISTING form-generated controls (text/number/checkbox/markdown/picker/dimension) read from
    /// and write straight back to the node — ONE source of truth, no <c>SetupAutoSave</c> replica +
    /// save-subscription. See <c>Doc/GUI/DataBinding</c> → "ABSOLUTE: edit node content by binding
    /// to the node stream".
    ///
    /// <para>Shape: <c>/$meshNode/{base64url(nodePath)}/{target}</c> where <c>{target}</c> is
    /// <see cref="MeshNodeContentTarget"/> ("c") to bind field pointers against the node's
    /// <c>Content</c> JSON, or <see cref="MeshNodeFieldsTarget"/> ("n") to bind against the whole
    /// node JSON (top-level fields like <c>Name</c>/<c>Description</c>/<c>Icon</c>/<c>Category</c>/
    /// <c>Order</c> + a nested <c>content/…</c> path). The path is base64url so it carries no
    /// <c>/</c>, <c>.</c>, or <c>%9Y</c> — surviving the <c>DispatchView</c> <c>WorkspaceReference.Decode</c>
    /// hop and JSON-pointer parsing untouched. Field-pointer resolution against the node is
    /// case-insensitive, so both PascalCase DTO pointers and camelCase content pointers bind.</para>
    /// </summary>
    public const string MeshNodePrefix = "$meshNode";

    /// <summary>Target token: bind field pointers against the node's <c>Content</c> JSON.</summary>
    public const string MeshNodeContentTarget = "c";

    /// <summary>Target token: bind field pointers against the whole node JSON (top-level fields).</summary>
    public const string MeshNodeFieldsTarget = "n";

    /// <summary>
    /// Builds a node-bound DataContext (see <see cref="MeshNodePrefix"/>) for the node at
    /// <paramref name="nodePath"/>. <paramref name="bindContent"/> selects whether the form's field
    /// pointers resolve against the node's <c>Content</c> JSON (the default — for content-typed
    /// editors) or against the whole node JSON (top-level fields — for metadata editors).
    /// <paramref name="subPath"/> optionally nests the binding root one level deeper (e.g.
    /// <c>"composer"</c> so a form's <c>harness</c> pointer resolves to <c>content/composer/harness</c>
    /// on a Thread node that embeds its composer) — every field pointer is resolved relative to it.
    /// </summary>
    public static string GetMeshNodeDataContext(string nodePath, bool bindContent = true, string? subPath = null)
    {
        var ctx = $"/{MeshNodePrefix}/{Base64UrlEncode(nodePath)}/{(bindContent ? MeshNodeContentTarget : MeshNodeFieldsTarget)}";
        return string.IsNullOrEmpty(subPath) ? ctx : $"{ctx}/{Base64UrlEncode(subPath)}";
    }

    /// <summary>
    /// Decodes a node-bound DataContext produced by <see cref="GetMeshNodeDataContext"/>. Returns
    /// <c>null</c> when <paramref name="dataContext"/> is not node-bound (an ordinary
    /// <c>/data/{id}</c> context), so the binding hot path can branch cheaply.
    /// </summary>
    public static (string NodePath, bool BindContent, string? SubPath)? TryParseMeshNodeDataContext(string? dataContext)
    {
        if (string.IsNullOrEmpty(dataContext))
            return null;
        var trimmed = dataContext.StartsWith('/') ? dataContext[1..] : dataContext;
        var parts = trimmed.Split('/');
        if (parts.Length is < 3 or > 4 || parts[0] != MeshNodePrefix)
            return null;
        var bindContent = parts[2] != MeshNodeFieldsTarget; // default to content for unknown token
        try
        {
            var subPath = parts.Length == 4 ? Base64UrlDecode(parts[3]) : null;
            return (Base64UrlDecode(parts[1]), bindContent, subPath);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Base64UrlEncode(string value)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    /// <summary>
    /// Gets the data pointer for the specified ID and extra segments.
    /// </summary>
    /// <param name="id">The ID for the data pointer.</param>
    /// <param name="extraSegments">The extra segments for the data pointer.</param>
    /// <returns>A string representing the data pointer.</returns>
    public static string GetDataPointer(string id, params string?[] extraSegments)
    {

        var segments = new[] { Data, Encode(id) }
            .Concat(extraSegments)
            .Where(x => x is not null);
        // Build JSON Pointer path manually: /segment1/segment2/...
        return "/" + string.Join("/", segments.Select(EncodePointerSegment));
    }

    private static string EncodePointerSegment(string? segment)
    {
        if (segment is null) return string.Empty;
        // RFC 6901: escape ~ as ~0 and / as ~1
        return segment.Replace("~", "~0").Replace("/", "~1");
    }

    /// <summary>
    /// JSON-encodes an id for use as a layout-area reference segment.
    /// </summary>
    /// <param name="id">The id to encode.</param>
    /// <returns>The JSON-encoded id.</returns>
    public static string Encode(string id)
        => JsonSerializer.Serialize(id);

    /// <summary>
    /// Gets the control pointer for the specified area.
    /// </summary>
    /// <param name="area">The area for the control pointer.</param>
    /// <returns>A string representing the control pointer.</returns>
    public static string GetControlPointer(string? area) =>
        JsonPointer.Create(Areas, JsonSerializer.Serialize(area ?? string.Empty)).ToString();


    /// <summary>
    /// Converts the layout area reference to an application href.
    /// </summary>
    /// <param name="address">The address for the href.</param>
    /// <returns>A string representing the application href.</returns>
    public string ToHref(object address)
    {
        if (Area == null)
            return address.ToString()!;
        var ret = $"{address}/{WorkspaceReference.Encode(Area)}";
        if (Id?.ToString() is { } s)
            ret = $"{ret}/{WorkspaceReference.Encode(s)}";
        return ret;
    }
    /// <summary>
    /// Converts the layout area reference to an application href.
    /// </summary>
    /// <param name="address">The address for the href.</param>
    /// <returns>A string representing the application href.</returns>
    public string ToHref(Address address)
    {
        if (Area is null)
            return address.ToString();
        var ret = $"{address}/{WorkspaceReference.Encode(Area)}";
        if (Id?.ToString() is { } s)
            ret = $"{ret}/{WorkspaceReference.Encode(s)}";
        return ret;
    }

    /// <summary>
    /// Builds an application href from the given address parts and this reference's area/id.
    /// </summary>
    /// <param name="addressType">The address type segment.</param>
    /// <param name="addressId">The address id segment.</param>
    /// <returns>The href in the form <c>addressType/addressId/areaName[/areaId]</c>.</returns>
    public string ToHref(string addressType, string addressId)
    {
        // Format: addressType/addressId/areaName[/areaId] (area is default, no keyword needed)
        var ret = $"{addressType}/{addressId}";
        if (Area is not null)
        {
            ret = $"{ret}/{WorkspaceReference.Encode(Area)}";
            if (Id?.ToString() is { } s)
                ret = $"{ret}/{WorkspaceReference.Encode(s)}";
        }
        return ret;
    }

    private readonly Lazy<IReadOnlyDictionary<string, string?>> parameters = new();
    /// <summary>
    /// Returns the value of the query parameter parsed from <see cref="Id"/>, or null if absent.
    /// </summary>
    /// <param name="name">The parameter name (case-insensitive).</param>
    /// <returns>The parameter value, or null.</returns>
    public string? GetParameterValue(string name)
    {
        return parameters.Value.GetValueOrDefault(name);
    }

    /// <summary>
    /// Returns true if the parameter with the given name is present in <see cref="Id"/>.
    /// </summary>
    /// <param name="name">The parameter name (case-insensitive).</param>
    /// <returns>True if the parameter is present; otherwise false.</returns>
    public bool HasParameter(string name) => parameters.Value.ContainsKey(name);

    // Override the generated Equals and GetHashCode to exclude the parameters field
    /// <summary>
    /// Determines equality by <see cref="Area"/>, <see cref="Id"/> and <see cref="Layout"/>,
    /// deliberately excluding the parsed parameters field.
    /// </summary>
    /// <param name="other">The reference to compare against.</param>
    /// <returns>True if the references are equal; otherwise false.</returns>
    public virtual bool Equals(LayoutAreaReference? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(Area, other.Area, StringComparison.Ordinal) &&
               IdEquals(Id, other.Id) &&
               string.Equals(Layout, other.Layout, StringComparison.Ordinal);
    }

    /// <summary>Returns a hash code derived from <see cref="Area"/>, <see cref="Id"/> and <see cref="Layout"/>.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        return HashCode.Combine(
            Area ?? string.Empty,
            NormalizeId(Id) ?? string.Empty,
            Layout ?? string.Empty
        );
    }

    // 🚨 Id is an `object?` that ROUND-TRIPS THROUGH JSON (the area reference is serialized to the
    // client and back). A deserialized Id is a System.Text.Json.JsonElement, which has NO structural
    // equality: `Equals(jsonElement, "v12")` and even `Equals(jsonElementA, jsonElementB)` for
    // byte-identical content are ALWAYS false. With the raw `Equals(Id, other.Id)` a freshly-built
    // reference (string Id) never equalled the deserialized one (JsonElement Id), so LayoutAreaView's
    // `!AreaStream.Reference.Equals(ViewModel.Reference)` was perpetually true → it disposed and
    // re-subscribed the whole area on every parent render → the 448×-render FullHeader storm that
    // saturated the circuit. Normalize both sides to their scalar string form before comparing.
    private static bool IdEquals(object? a, object? b)
        => ReferenceEquals(a, b)
           || string.Equals(NormalizeId(a), NormalizeId(b), StringComparison.Ordinal);

    private static string? NormalizeId(object? id) => id switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Null } => null,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        JsonElement je => je.GetRawText(),
        _ => id.ToString()
    };
}
