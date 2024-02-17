namespace OpenSmc.ServiceProvider;

public interface IModuleInitialization
{
    public void AddScopesDataCubes(IServiceProvider serviceProvider);
}