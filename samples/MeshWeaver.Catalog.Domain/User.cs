namespace MeshWeaver.Catalog.Domain
{
    public record User(string Id, string Email, string Name)
    {
        public string Avatar { get; init; }
        public string Bio { get; init; }
        public int Followers { get; init; }
        public int Following { get; init; }
    }
}
