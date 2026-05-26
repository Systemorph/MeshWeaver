namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Declares a permission flag for a node type.
/// Register as a singleton in DI from AddUserType(), AddSpaceType(), etc.
/// The persistence layer reads all registered instances at startup and writes
/// them to the database (e.g. node_type_permissions table in PostgreSQL).
/// </summary>
/// <param name="NodeType">The node type identifier (e.g. "User", "Space")</param>
/// <param name="PublicRead">When true, any authenticated user can read nodes of this type without explicit grants</param>
public record NodeTypePermission(string NodeType, bool PublicRead);
