namespace OpenSmc.Blazor;

public record ViewDescriptor(Type Type, IReadOnlyDictionary<string, object> Parameters);
