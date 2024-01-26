namespace OpenSmc.ServiceProvider;

public interface IModuleInitialization
{
    public void Initialize(IServiceProvider serviceProvider);
}