using MeshWeaver.ShortGuid;

namespace MeshWeaver.Messaging;

public record Address(string Type, params string[] Segments)
{
    /// <summary>
    /// Backward compatibility: Id returns all segments joined with "/"
    /// </summary>
    public string Id => string.Join("/", Segments);

    /// <summary>
    /// Host address for hosted/nested addresses.
    /// When set, ToFullString() returns "host@this" format.
    /// </summary>
    public Address? Host { get; init; }

    public sealed override string ToString() => Segments.Length > 0
        ? $"{Type}/{string.Join('/', Segments)}"
        : Type;

    /// <summary>
    /// Returns full string representation including host if present.
    /// Format: "host-type/host-segments@inner-type/inner-segments"
    /// </summary>
    public string ToFullString() => Host != null
        ? $"{Host}@{this}"
        : ToString();

    public virtual bool Equals(Address? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Type != other.Type) return false;
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

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Type);
        foreach (var segment in Segments)
            hash.Add(segment);
        if (Host != null)
            hash.Add(Host);
        return hash.ToHashCode();
    }

    public static implicit operator string?(Address? address) => address?.ToFullString();

    public static implicit operator Address(string address)
    {
        // First check for @ separator (hosted address)
        var atIndex = address.IndexOf('@');
        if (atIndex > 0)
        {
            var hostPart = address[..atIndex];
            var innerPart = address[(atIndex + 1)..];
            var host = ParseSimple(hostPart);
            var inner = ParseSimple(innerPart);
            return inner with { Host = host };
        }
        return ParseSimple(address);
    }

    private static Address ParseSimple(string address)
    {
        var parts = address.Split('/');
        return new Address(parts[0], parts.Length > 1 ? parts[1..] : []);
    }
}

public static class AddressExtensions
{
    public const string MeshType = "mesh";
    public const string AppType = "app";
    public const string UiType = "ui";
    public const string SignalRType = "signalr";
    public const string KernelType = "kernel";
    public const string NotebookType = "nb";
    public const string ArticlesType = "articles";
    public const string PortalType = "portal";
    public const string ActivityType = "activity";
    public const string PersistenceType = "persistence";
    public const string LayoutExecutionType = "le";

    public static Address CreateMeshAddress(string? id = null) =>
        new(MeshType, id ?? Guid.NewGuid().AsString());

    public static Address CreateAppAddress(string id) => new(AppType, id);
    public static Address CreateUiAddress(string? id = null) => new(UiType, id ?? Guid.NewGuid().AsString());
    public static Address CreateSignalRAddress(string? id = null) => new(SignalRType, id ?? Guid.NewGuid().AsString());
    public static Address CreateKernelAddress(string? id = null) => new(KernelType, id ?? Guid.NewGuid().AsString());
    public static Address CreateNotebookAddress(string? id = null) => new(NotebookType, id ?? Guid.NewGuid().AsString());
    public static Address CreateArticlesAddress(string id) => new(ArticlesType, id);
    public static Address CreatePortalAddress(string? id = null) => new(PortalType, id ?? Guid.NewGuid().AsString());
    public static Address CreateActivityAddress(string? id = null) => new(ActivityType, id ?? Guid.NewGuid().AsString());
    public static Address CreatePersistenceAddress(string? id = null) => new(PersistenceType, id ?? Guid.NewGuid().AsString());
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
public class AddressComparer : IEqualityComparer<Address>
{
    internal static readonly AddressComparer Instance = new();

    public bool Equals(Address? x, Address? y)
    {
        if (ReferenceEquals(x, y))
            return true;
        if (x is null || y is null)
            return false;
        return x.Type.Equals(y.Type) && x.Id.Equals(y.Id);
    }

    public int GetHashCode(Address obj)
    {
        return HashCode.Combine(obj.Type, obj.Id);
    }
}
