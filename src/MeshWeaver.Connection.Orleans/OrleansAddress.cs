using MeshWeaver.ShortGuid;

namespace MeshWeaver.Connection.Orleans
{
    public record OrleansAddress
    {
        public string Id { get; set; } = Guid.NewGuid().AsString();

        public override string ToString()
            => $"o_{Id}";
    }
}
