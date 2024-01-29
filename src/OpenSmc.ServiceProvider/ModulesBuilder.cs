using System.Reflection;

namespace OpenSmc.ServiceProvider;

public record Modules(List<IModuleRegistry> Registries, List<IModuleInitialization> Initializations);

public class ModulesBuilder
{
    internal ExpansionMode ExpansionMode { get; set; }
    private readonly List<object> modules = new();
    private readonly List<Assembly> includeAssemblies = new();
    private readonly List<Assembly> excludeAssemblies = new();


    public ModulesBuilder WithExpansionMode(ExpansionMode mode)
    {
        ExpansionMode = mode;
        return this;
    }

    public ModulesBuilder Clear()
    {
        includeAssemblies.Clear();
        return this;
    }

    public ModulesBuilder Add<T>()
        where T : new()
    {
        modules.Add(new T());
        return this;
    }

    public ModulesBuilder Add(params Assembly[] assemblies)
    {
        includeAssemblies.AddRange(assemblies);
        return this;
    }
    public ModulesBuilder Remove(params Assembly[] assemblies)
    {
        excludeAssemblies.AddRange(assemblies);
        return this;
    }

    private static bool IsOpenSmcModule(string fullName)
    {
        // TODO V10: How to unhardcode? (2023/05/21, Roland Buergi)
        return fullName.StartsWith("OpenSmc");
    }

    private static IEnumerable<Assembly> ExpandedModuleAssemblies(IEnumerable<Assembly> assemblies, HashSet<Assembly> expandedSet)
    {
        foreach (var assembly in assemblies.Where(a => IsOpenSmcModule(a.FullName)))
        {
            if (expandedSet.Contains(assembly))
                continue;
            expandedSet.Add(assembly);
            var references = new HashSet<string>(assembly.GetReferencedAssemblies().Select(n => n.FullName).Where(IsOpenSmcModule));
            var referencedAssemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => references.Contains(a.FullName)).ToList();
            references.ExceptWith(referencedAssemblies.Select(a => a.FullName));
            foreach (var reference in references)
                referencedAssemblies.Add(Assembly.Load(reference));

            foreach (var da in ExpandedModuleAssemblies(referencedAssemblies, expandedSet))
            {
                yield return da;
            }
            yield return assembly;
        }
    }

    public Modules Build()
    {
        List<IModuleRegistry> registries = new();
        List<IModuleInitialization> initializations = new();

        foreach (object module in modules)
        {
            if (module is IModuleRegistry mr)
                registries.Add(mr);
            if (module is IModuleInitialization mi)
                initializations.Add(mi);
        }

        var assemblies =
            ExpansionMode switch
            {
                ExpansionMode.ExpandAllReferences => ExpandedModuleAssemblies(includeAssemblies, new(excludeAssemblies)).ToList(),
                _ => includeAssemblies
            };

        foreach (var assembly in assemblies)
        {
            foreach (var attribute in assembly.GetCustomAttributes())
            {
                if (attribute is IModuleRegistry mr)
                    registries.Add(mr);
                if (attribute is IModuleInitialization mi)
                    initializations.Add(mi);
            }
        }

        return new Modules(registries, initializations);
    }
}