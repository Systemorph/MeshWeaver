namespace MeshWeaver.TestDomain.Scopes
{
    public record GuidIdentity
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    public record IdentitiesStorage
    {
        public IList<GuidIdentity> Identities { get; }

        public IdentitiesStorage(int nIdentities)
        {
            Identities = Enumerable.Range(0, nIdentities).Select(_ => new GuidIdentity()).ToArray();
        }
    }
}