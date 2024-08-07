using System.ComponentModel.DataAnnotations;
using MeshWeaver.Arithmetics.Aggregation;
using MeshWeaver.DataCubes;
using MeshWeaver.Domain;
using MeshWeaver.Scopes;
using MeshWeaver.TestDomain.Cubes;

namespace MeshWeaver.TestDomain.Scopes;

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
