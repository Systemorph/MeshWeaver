using MeshWeaver.ShortGuid;

namespace MeshWeaver.Orleans.Client
{
    public record OrleansAddress
    {
        public string Id { get; set; } = Guid.NewGuid().AsString();

        public override string ToString()
            => $"o_{Id}";
    }
}
