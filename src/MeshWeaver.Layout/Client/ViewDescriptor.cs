namespace MeshWeaver.Layout.Client;

/// <summary>
/// Describes a Blazor component type and its parameter dictionary, used by the client-side
/// renderer to instantiate the correct component for a given UI control.
/// </summary>
/// <param name="Type">The Blazor component type to render.</param>
/// <param name="Parameters">The parameter values keyed by parameter name to pass to the component.</param>
public record ViewDescriptor(Type Type, IDictionary<string, object?> Parameters);
