using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ServiceProvider;

public class LoadedModulesService
{
    internal HashSet<Type> Types = new();

    [ActivatorUtilitiesConstructor]
    public LoadedModulesService()
    {
    }

    public LoadedModulesService(IEnumerable<Type> types)
    {
    }

    public IReadOnlyCollection<Type> GetLoadedModules()
    {
        return Types.ToArray();
    }
}
