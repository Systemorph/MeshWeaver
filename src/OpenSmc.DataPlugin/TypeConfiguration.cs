namespace OpenSmc.DataPlugin;

public abstract record TypeConfiguration();

public record TypeConfiguration<T>(
    Func<Task<IReadOnlyCollection<T>>> Initialize,
    Func<IReadOnlyCollection<T>, Task> Save,
    Func<IReadOnlyCollection<object>, Task> Delete) : TypeConfiguration;