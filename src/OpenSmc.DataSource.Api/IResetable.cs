
namespace OpenSmc.DataSource.Api;

public interface IResetable
{
    void Reset(ResetOptions options = default);
}