namespace MeshWeaver.Domain
{
    /// <summary>
    /// Implemented by types that expose a human-readable display name.
    /// </summary>
    public interface INamed
    {
        /// <summary>
        /// The human-readable name used to display the instance.
        /// </summary>
        string DisplayName { get; }
    }
}
