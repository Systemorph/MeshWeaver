using System.ComponentModel.DataAnnotations;
using OpenSmc.DataCubes;
using OpenSmc.Scopes;

namespace OpenSmc.TestDomain.Scopes
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