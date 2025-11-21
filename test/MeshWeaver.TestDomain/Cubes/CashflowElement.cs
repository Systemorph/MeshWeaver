using MeshWeaver.Domain;

namespace MeshWeaver.TestDomain.Cubes;

public record CashflowElementWithInt : CashflowElement
{
    [NotVisible]
    [Dimension(typeof(int), nameof(Year))]
    [IdentityProperty]
    public int Year { get; init; }
}

// TODO: Should DimensionAttribute inherit from IdentityPropertyAttribute? (2021/05/19, Roland Buergi)
public record CashflowElement : IHasValue
{
    [NotVisible]
    [Dimension(typeof(LineOfBusiness))]
    [IdentityProperty]
    public string? LineOfBusiness { get; init; }

    [NotVisible]
    [Dimension(typeof(Country))]
    [IdentityProperty]
    public string? Country { get; init; }

    [NotVisible]
    [Dimension(typeof(AmountType))]
    [IdentityProperty]
    public string? AmountType { get; init; }

    [NotVisible]
    [Dimension(typeof(Scenario))]
    [IdentityProperty]
    public string? Scenario { get; init; }

    [NotVisible]
    [Dimension(typeof(Split))]
    [IdentityProperty]
    public string? Split { get; init; }

    [NotVisible]
    [Dimension(typeof(Currency))]
    [IdentityProperty]
    public string? Currency { get; init; }

    public double Value { get; init; }
}
