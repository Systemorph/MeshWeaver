using MeshWeaver.ShortGuid;

namespace MeshWeaver.Messaging;

public record Address(string Type, string Id)
{
    public sealed override string ToString() => $"{Type}/{Id}";

    public virtual bool Equals(Address? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return Type == other.Type && (Id ?? string.Empty) == (other.Id ?? string.Empty);
    }

    public override int GetHashCode() => HashCode.Combine(Type, Id);

    public static implicit operator string?(Address? address) => address?.ToString();

    public static implicit operator Address(string address)
    {
        var parts = address.Split('/');
        return new Address(parts[0], parts.Length > 1 ? string.Join('/', parts.Skip(1)) : string.Empty);
    }
}
public record MeshAddress(string? Id = null) : Address(MeshAddress.TypeName, Id ?? Guid.NewGuid().AsString())
{
    public const string TypeName = "mesh";
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
