namespace MeshWeaver.Messaging;

public record Address(string Type, string Id)
{
    public sealed override string ToString() => $"{Type}/{Id}";
    public virtual bool Equals(Address other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return Type == other.Type && Id == other.Id;
    }

    public override int GetHashCode() => HashCode.Combine(Type, Id);
}
