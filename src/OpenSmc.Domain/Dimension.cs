namespace OpenSmc.Domain;

public record Dimension : INamed
{
    [IdentityProperty]
    public string SystemName { get; init; }
    public string DisplayName { get; init; }
}
