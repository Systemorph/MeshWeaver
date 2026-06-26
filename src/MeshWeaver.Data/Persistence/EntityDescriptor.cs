namespace MeshWeaver.Data.Persistence;

/// <summary>
/// Describes a single entity together with the collection it belongs to and its identity.
/// Used when persisting or transferring individual entities between stores.
/// </summary>
/// <param name="Collection">The name of the collection the entity belongs to.</param>
/// <param name="Id">The identity (key) of the entity within its collection.</param>
/// <param name="Entity">The entity instance being described.</param>
public record EntityDescriptor(string Collection, object Id, object Entity);