namespace MeshWeaver.Messaging;

public record Address(string Type, string Id)
{
    public sealed override string ToString() => $"{Type}/{Id}";

    public virtual bool Equals(Address other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return Type == other.Type && (Id ?? string.Empty) == (other.Id ?? string.Empty);
    }

    public override int GetHashCode() => HashCode.Combine(Type, Id);

    public static implicit operator string(Address address) => address.ToString();

    public static implicit operator Address(string address)
    {
        var parts = address.Split('/');
        return new Address(parts[0], parts.Length > 1 ? string.Join('/', parts.Skip(1)) : string.Empty);
    }
}
