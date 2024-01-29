using System.ComponentModel.DataAnnotations;
using OpenSmc.Arithmetics.Aggregation;
using OpenSmc.DataCubes;
using OpenSmc.Domain.Abstractions.Attributes;
using OpenSmc.Scopes;
using OpenSmc.TestDomain.Cubes;

namespace OpenSmc.TestDomain.Scopes
{
    public interface IScopeWithDataCubeProperties :
        IScope<YearQuarterAndCompany, YearAndQuarterAndCompanyIdentityStorage>, IDataCube<CashflowElement>
    {
        [Display(Name = "Beginning of Period")]
        IDataCube<CashflowElement> BoP => CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube();

        IDataCube<CashflowElement> Delta => CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube();

        [Display(Name = "End of Period")]
        IDataCube<CashflowElement> EoP => SumFunction.Sum(BoP, Delta);
    }
    
    public interface IScopeWithInvisibleDataCubeProperties :
        IScope<YearQuarterAndCompany, YearAndQuarterAndCompanyIdentityStorage>, IDataCube<CashflowElement>
    {
        [Display(Name = "Beginning of Period")]
        IDataCube<CashflowElement> BoP => CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube();

        [NotVisible]
        IDataCube<CashflowElement> Delta => CashflowFactory.GenerateEquallyWeightedAllPopulated().ToDataCube();

        [Display(Name = "End of Period")]
        IDataCube<CashflowElement> EoP => SumFunction.Sum(BoP, Delta);
    }
}