using MeshWeaver.ShortGuid;

namespace MeshWeaver.Messaging;

/// <summary>
/// Unified address consisting of path segments.
/// Example: "pricing/Microsoft/2026" has segments ["pricing", "Microsoft", "2026"]
/// </summary>
public sealed record Address
{
    /// <summary>
    /// Unified address consisting of path segments.
    /// Example: "pricing/Microsoft/2026" has segments ["pricing", "Microsoft", "2026"]
    /// </summary>
    public Address(params string[] Segments)
    {
        this.Segments = Segments.Length == 1 ? Segments[0].Split('/', StringSplitOptions.RemoveEmptyEntries) : Segments;
    }

    /// <summary>
    /// Gets the first segment (typically the type/namespace).
    /// </summary>
    public string Type => Segments.Length > 0 ? Segments[0] : "";

    /// <summary>
    /// Gets segments after the first one joined with "/".
    /// For backward compatibility.
    /// </summary>
    public string Id => Segments.Length > 1
        ? string.Join("/", Segments.Skip(1))
        : "";

    /// <summary>
    /// Host address for hosted/nested addresses.
    /// When set, ToFullString() returns "host~this" format.
    /// </summary>
    public Address? Host { get; init; }

    /// <summary>
    /// The ordered path segments that make up this address.
    /// </summary>
    public string[] Segments { get; init; }

    /// <summary>
    /// Returns the path (segments joined with "/") without host information.
    /// Use this instead of ToString() when you need the node path for persistence or display.
    /// </summary>
    public string Path => string.Join("/", Segments);

    /// <summary>
    /// Returns the segment path, appending <c>~host</c> when this address is hosted.
    /// </summary>
    public override string ToString() => Host is null
        ? string.Join("/", Segments)
        : string.Join("/", Segments) + '~' + Host;

    /// <summary>
    /// Returns full string representation including host if present.
    /// Format: "outermost_host~...~inner_segments"
    /// </summary>
    public string ToFullString() => Host != null
        ? $"{Host.ToFullString()}~{string.Join("/", Segments)}"
        : string.Join("/", Segments);

    /// <summary>
    /// Two addresses are equal when their segments match in order and their hosts
    /// are equal (or both absent).
    /// </summary>
    /// <param name="other">The address to compare with.</param>
    /// <returns>True if the addresses are equal.</returns>
    public bool Equals(Address? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Segments.Length != other.Segments.Length) return false;
        for (int i = 0; i < Segments.Length; i++)
        {
            if (Segments[i] != other.Segments[i]) return false;
        }
        // Also compare Host
        if (Host is null != other.Host is null) return false;
        if (Host is not null && !Host.Equals(other.Host)) return false;
        return true;
    }

    /// <summary>
    /// Computes a hash code from the segments and host, consistent with <see cref="Equals(Address?)"/>.
    /// </summary>
    /// <returns>A hash code for this address.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var segment in Segments)
            hash.Add(segment);
        if (Host != null)
            hash.Add(Host);
        return hash.ToHashCode();
    }

    /// <summary>
    /// Converts an address to its full string representation (including host), or null.
    /// </summary>
    /// <param name="address">The address to convert.</param>
    public static implicit operator string?(Address? address) => address?.ToFullString();

    /// <summary>
    /// Parses a string into an <see cref="Address"/>, splitting on "/" for segments
    /// and on "~" for the host chain.
    /// </summary>
    /// <param name="address">The string to parse.</param>
    public static implicit operator Address(string address)
    {
        // First check for ~ separator (hosted address format: "outermost_host~...~inner")
        var tildeIndex = address.IndexOf('~');
        if (tildeIndex > 0)
        {
            var hostPart = address[..tildeIndex];
            var innerPart = address[(tildeIndex + 1)..];
            Address host = Parse(hostPart);
            Address inner = innerPart; // Recursive: handles nested ~ via implicit operator
            return inner.WithHost(host);
        }
        return Parse(address);
    }

    private static Address Parse(string address) =>
        new(address.Split('/'));

    /// <summary>
    /// Deconstructs the address into its segments.
    /// </summary>
    /// <param name="Segments">Receives the path segments.</param>
    public void Deconstruct(out string[] Segments)
    {
        Segments = this.Segments;
    }
}

/// <summary>
/// Well-known address type prefixes and factory helpers for constructing the
/// standard mesh, UI, and infrastructure addresses.
/// </summary>
public static class AddressExtensions
{
    /// <summary>Type prefix for the mesh hub address.</summary>
    public const string MeshType = "mesh";
    /// <summary>Type prefix for an application address.</summary>
    public const string AppType = "app";
    /// <summary>Type prefix for a UI (Blazor circuit) address.</summary>
    public const string UiType = "ui";
    /// <summary>Type prefix for a SignalR connection address.</summary>
    public const string SignalRType = "signalr";
    /// <summary>Type prefix for a kernel (REPL) address.</summary>
    public const string KernelType = "kernel";
    /// <summary>Type prefix for an MCP server address.</summary>
    public const string McpType = "mcp";
    /// <summary>Type prefix for a notebook address.</summary>
    public const string NotebookType = "nb";
    /// <summary>Type prefix for an articles address.</summary>
    public const string ArticlesType = "articles";
    /// <summary>Type prefix for a portal address.</summary>
    public const string PortalType = "portal";
    /// <summary>Type prefix for an activity address.</summary>
    public const string ActivityType = "activity";
    /// <summary>Type prefix for a persistence hub address.</summary>
    public const string PersistenceType = "persistence";
    /// <summary>Type prefix for a layout-execution hub address.</summary>
    public const string LayoutExecutionType = "le";

    /// <summary>
    /// Creates a mesh address, generating a random id when none is given.
    /// </summary>
    /// <param name="id">Optional id; a new GUID is used when null.</param>
    /// <returns>The mesh address.</returns>
    public static Address CreateMeshAddress(string? id = null) =>
        new(MeshType, id ?? Guid.NewGuid().AsString());

    /// <summary>
    /// Creates an application address with the given id.
    /// </summary>
    /// <param name="id">The application id.</param>
    /// <returns>The application address.</returns>
    public static Address CreateAppAddress(string id) => new(AppType, id);
    /// <summary>
    /// Creates a UI address, generating a random id when none is given.
    /// </summary>
    /// <param name="id">Optional id; a new GUID is used when null.</param>
    /// <returns>The UI address.</returns>
    public static Address CreateUiAddress(string? id = null) => new(UiType, id ?? Guid.NewGuid().AsString());
    /// <summary>
    /// Creates a SignalR address, generating a random id when none is given.
    /// </summary>
    /// <param name="id">Optional id; a new GUID is used when null.</param>
    /// <returns>The SignalR address.</returns>
    public static Address CreateSignalRAddress(string? id = null) => new(SignalRType, id ?? Guid.NewGuid().AsString());
    /// <summary>
    /// Creates a kernel address, generating a random id when none is given.
    /// </summary>
    /// <param name="id">Optional id; a new GUID is used when null.</param>
    /// <returns>The kernel address.</returns>
    public static Address CreateKernelAddress(string? id = null) => new(KernelType, id ?? Guid.NewGuid().AsString());
    /// <summary>
    /// Creates an MCP address, generating a random id when none is given.
    /// </summary>
    /// <param name="id">Optional id; a new GUID is used when null.</param>
    /// <returns>The MCP address.</returns>
    public static Address CreateMcpAddress(string? id = null) => new(McpType, id ?? Guid.NewGuid().AsString());
    /// <summary>
    /// Creates a notebook address, generating a random id when none is given.
    /// </summary>
    /// <param name="id">Optional id; a new GUID is used when null.</param>
    /// <returns>The notebook address.</returns>
    public static Address CreateNotebookAddress(string? id = null) => new(NotebookType, id ?? Guid.NewGuid().AsString());
    /// <summary>
    /// Creates an articles address with the given id.
    /// </summary>
    /// <param name="id">The articles id.</param>
    /// <returns>The articles address.</returns>
    public static Address CreateArticlesAddress(string id) => new(ArticlesType, id);
    /// <summary>
    /// Creates a portal address, generating a random id when none is given.
    /// </summary>
    /// <param name="id">Optional id; a new GUID is used when null.</param>
    /// <returns>The portal address.</returns>
    public static Address CreatePortalAddress(string? id = null) => new(PortalType, id ?? Guid.NewGuid().AsString());
    /// <summary>
    /// Creates an activity address, generating a random id when none is given.
    /// </summary>
    /// <param name="id">Optional id; a new GUID is used when null.</param>
    /// <returns>The activity address.</returns>
    public static Address CreateActivityAddress(string? id = null) => new(ActivityType, id ?? Guid.NewGuid().AsString());
    /// <summary>
    /// Creates a persistence address, generating a random id when none is given.
    /// </summary>
    /// <param name="id">Optional id; a new GUID is used when null.</param>
    /// <returns>The persistence address.</returns>
    public static Address CreatePersistenceAddress(string? id = null) => new(PersistenceType, id ?? Guid.NewGuid().AsString());
    /// <summary>
    /// Creates a layout-execution address, generating a random id when none is given.
    /// </summary>
    /// <param name="id">Optional id; a new GUID is used when null.</param>
    /// <returns>The layout-execution address.</returns>
    public static Address CreateLayoutExecutionAddress(string? id = null) => new(LayoutExecutionType, id ?? Guid.NewGuid().AsString());

    /// <summary>
    /// Creates a hosted address where innerAddress is hosted by hostAddress.
    /// If innerAddress already has a host, the new hostAddress wraps the existing host chain.
    /// </summary>
    public static Address WithHost(this Address innerAddress, Address hostAddress) =>
        innerAddress.Host is null
            ? innerAddress with { Host = hostAddress }
            : innerAddress with { Host = innerAddress.Host.WithHost(hostAddress) };

    /// <summary>
    /// Checks if this address has a host (is a hosted/nested address).
    /// </summary>
    public static bool IsHosted(this Address address) => address.Host != null;
}
/// <summary>
/// Equality comparer that compares addresses by their <see cref="Address.Type"/>
/// and <see cref="Address.Id"/> only (ignoring deeper segment structure and host).
/// </summary>
public class AddressComparer : IEqualityComparer<Address>
{
    internal static readonly AddressComparer Instance = new();

    /// <summary>
    /// Determines whether two addresses share the same type and id.
    /// </summary>
    /// <param name="x">The first address.</param>
    /// <param name="y">The second address.</param>
    /// <returns>True if both are null or have matching type and id.</returns>
    public bool Equals(Address? x, Address? y)
    {
        if (ReferenceEquals(x, y))
            return true;
        if (x is null || y is null)
            return false;
        return x.Type.Equals(y.Type) && x.Id.Equals(y.Id);
    }

    /// <summary>
    /// Computes a hash code from the address's type and id, consistent with <see cref="Equals(Address?, Address?)"/>.
    /// </summary>
    /// <param name="obj">The address to hash.</param>
    /// <returns>A hash code combining the type and id.</returns>
    public int GetHashCode(Address obj)
    {
        return HashCode.Combine(obj.Type, obj.Id);
    }
}
