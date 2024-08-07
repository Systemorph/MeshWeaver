using System.ComponentModel.DataAnnotations;
using MeshWeaver.Arithmetics.Aggregation;
using MeshWeaver.DataCubes;
using MeshWeaver.Domain;
using MeshWeaver.Scopes;
using MeshWeaver.TestDomain.Cubes;

namespace MeshWeaver.TestDomain.Scopes;

public interface IScopeWithDataCubeProperties
    : IScope<YearQuarterAndCompany, YearAndQuarterAndCompanyIdentityStorage>,
        IDataCube<CashflowElement>
{
    [Display(Name = "Beginning of Period")]
    IDataCube<CashflowElement> BoP =>
        CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube();

    IDataCube<CashflowElement> Delta =>
        CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube();

    [Display(Name = "End of Period")]
    IDataCube<CashflowElement> EoP => SumFunction.Sum(BoP, Delta);
}

public interface IScopeWithInvisibleDataCubeProperties
    : IScope<YearQuarterAndCompany, YearAndQuarterAndCompanyIdentityStorage>,
        IDataCube<CashflowElement>
{
    [Display(Name = "Beginning of Period")]
    IDataCube<CashflowElement> BoP =>
        CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube();

    [NotVisible]
    IDataCube<CashflowElement> Delta =>
        CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube();

    [Display(Name = "End of Period")]
    IDataCube<CashflowElement> EoP => SumFunction.Sum(BoP, Delta);
}
