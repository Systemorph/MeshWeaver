namespace OpenSmc.Domain.Abstractions
{
    public interface INamed
    {
        string SystemName { get; }
        string DisplayName { get; }
    }
}
