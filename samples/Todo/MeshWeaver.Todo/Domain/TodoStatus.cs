namespace MeshWeaver.Todo.Domain;

/// <summary>
/// Represents the status of a todo item
/// </summary>
public enum TodoStatus
{
    /// <summary>
    /// The todo item is pending and has not been started
    /// </summary>
    Pending = 0,
    
    /// <summary>
    /// The todo item is currently in progress
    /// </summary>
    InProgress = 1,
    
    /// <summary>
    /// The todo item has been completed
    /// </summary>
    Completed = 2,
    
    /// <summary>
    /// The todo item has been cancelled
    /// </summary>
    Cancelled = 3
}
