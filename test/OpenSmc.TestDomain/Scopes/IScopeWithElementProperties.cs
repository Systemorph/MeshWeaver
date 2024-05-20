using System.ComponentModel.DataAnnotations;
using OpenSmc.Arithmetics.Aggregation;
using OpenSmc.DataCubes;
using OpenSmc.Domain;
using OpenSmc.Scopes;
using OpenSmc.TestDomain.Cubes;

namespace OpenSmc.TestDomain.Scopes;

public interface IScopeWithElementProperties
    : IScope<YearQuarterAndCompany, YearAndQuarterAndCompanyIdentityStorage>,
        IDataCube<CashflowElement>
{
    [Display(Name = "Beginning of Period")]
    CashflowElement BoP =>
        new CashflowElement
        {
            Country = Country.Data.First().SystemName,
            AmountType = AmountType.Data.First().SystemName,
            Currency = Currency.Data.First().SystemName,
            Value = 0
        };

    CashflowElement Delta =>
        new CashflowElement
        {
            Country = Country.Data.First().SystemName,
            AmountType = AmountType.Data.First().SystemName,
            Currency = Currency.Data.First().SystemName,
            Value = 1
        };

    [Display(Name = "End of Period")]
    CashflowElement EoP => AggregationFunction.Aggregate(BoP, Delta);
}

public interface IScopeWithInvisibleElementProperties
    : IScope<YearQuarterAndCompany, YearAndQuarterAndCompanyIdentityStorage>,
        IDataCube<CashflowElement>
{
    [Display(Name = "Beginning of Period")]
    CashflowElement BoP =>
        new CashflowElement
        {
            Country = Country.Data.First().SystemName,
            AmountType = AmountType.Data.First().SystemName,
            Currency = Currency.Data.First().SystemName,
            Value = 0
        };

    [NotVisible]
    CashflowElement Delta =>
        new CashflowElement
        {
            Country = Country.Data.First().SystemName,
            AmountType = AmountType.Data.First().SystemName,
            Currency = Currency.Data.First().SystemName,
            Value = 1
        };

    [Display(Name = "End of Period")]
    CashflowElement EoP => AggregationFunction.Aggregate(BoP, Delta);
}
