using System.ComponentModel.DataAnnotations;
using MeshWeaver.DataCubes;
using MeshWeaver.Scopes;

namespace MeshWeaver.TestDomain.Scopes
{
    public interface IDataCubeScope : IScope<YearQuarterAndCompany, YearAndQuarterAndCompanyIdentityStorage>, IDataCube<IDataCubeScope>
    {
        [Display(Name = "Beginning of Period")]
        double BoP => 1;

        double Delta => 1;

        [Display(Name = "End of Period")]
        double EoP => 1;
    }
}