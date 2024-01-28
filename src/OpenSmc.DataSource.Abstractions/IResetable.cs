
namespace OpenSmc.DataSource.Abstractions;

public interface IResetable
{
    void Reset(ResetOptions options = default);
}