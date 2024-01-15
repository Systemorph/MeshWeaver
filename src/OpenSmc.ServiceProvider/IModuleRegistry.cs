using Microsoft.Extensions.DependencyInjection;

namespace OpenSmc.ServiceProvider;

public interface IModuleRegistry
{
    public void Register(IServiceCollection services);
}