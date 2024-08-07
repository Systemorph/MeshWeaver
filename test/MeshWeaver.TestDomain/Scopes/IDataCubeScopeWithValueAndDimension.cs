using MeshWeaver.DataCubes;
using MeshWeaver.Domain;
using MeshWeaver.Scopes;
using MeshWeaver.TestDomain.Cubes;

namespace MeshWeaver.TestDomain.Scopes;

public interface IDataCubeScopeWithValueAndDimension
    : IScope<YearQuarterAndCompany, YearAndQuarterAndCompanyIdentityStorage>,
        IDataCube<IDataCubeScopeWithValueAndDimension>
{
    [Dimension(typeof(Country))]
    [NotVisible]
    public string Country => Identity.Company;

    public double Value => 1;
}

public interface IDataCubeScopeWithValueAndDimensionErr
    : IScope<YearQuarterAndCompany, YearAndQuarterAndCompanyIdentityStorage>,
        IDataCube<CashflowElement>
{
    [Dimension(typeof(Country), nameof(Country))]
    [NotVisible]
    public string Country1 => Identity.Company;

    public double Value => 1;
}

public interface IDataCubeScopeWithValueAndDimensionErr1
    : IScope<YearQuarterAndCompany, YearAndQuarterAndCompanyIdentityStorage>,
        IDataCube<CashflowElement>
{
    [Dimension(typeof(Country), "MyCountry")]
    [NotVisible]
    public string Country1 => Identity.Company;

    [Dimension(typeof(Country), "MyCountry")]
    [NotVisible]
    public string Country2 => Identity.Company;

    public double Value => 1;
}

// TODO V10: does this make sense? (2022/07/19, Ekaterina Mishina)
//public interface IDataCubeScopeWithValueAndDimension1 : IScope<YearQuarterAndCompany, YearAndQuarterAndCompanyIdentityStorage>, IDataCube<IDataCubeScopeWithValueAndDimension1>
//{
//    [Dimension(typeof(Country))]
//    [NotVisible]
//    public string Country => Identity.Company;

//    IDataCubeScopeWithValueAndDimension1 ScopeProperty { get; init; }

//    IDataCube<IDataCubeScopeWithValueAndDimension1> DataCubeScopeProperty { get; init; }

//    public double Value => 1;
//}

public interface IDataCubeScopeWithValueAndDimension2
    : IScope<YearQuarterAndCompany, YearAndQuarterAndCompanyIdentityStorage>,
        IDataCube<CashflowElement>
{
    [Dimension(typeof(Country), nameof(ScopeCountry))]
    [NotVisible]
    public string ScopeCountry => Identity.Company;

    CashflowElement ElementProperty =>
        new CashflowElement
        {
            Country = Country.Data.First().SystemName,
            AmountType = AmountType.Data.First().SystemName,
            Currency = Currency.Data.First().SystemName,
            Value = 1
        };

    IDataCube<CashflowElement> DataCubeScopeProperty =>
        CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube();
}

public interface IDataCubeScopeWithValueAndDimension3
    : IScope<YearQuarterAndCompany, YearAndQuarterAndCompanyIdentityStorage>,
        IDataCube<CashflowElement>
{
    [Dimension(typeof(Country), nameof(ScopeCountry))]
    [NotVisible]
    public string ScopeCountry => Identity.Company;

    IDataCube<CashflowElement> DataCubeScopeProperty =>
        CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube();
}

public interface IDataCubeScopeWithValueAndDimension4
    : IScope<YearQuarterAndCompany, YearAndQuarterAndCompanyIdentityStorage>,
        IDataCube<CashflowElement>
{
    [Dimension(typeof(Country), nameof(ScopeCountry))]
    [NotVisible]
    public string ScopeCountry => Identity.Company;

    CashflowElement ElementProperty =>
        new CashflowElement
        {
            Country = Country.Data.First().SystemName,
            AmountType = AmountType.Data.First().SystemName,
            Currency = Currency.Data.First().SystemName,
            Value = 1
        };
}
