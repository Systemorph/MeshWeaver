namespace MeshWeaver.Domain
{
    /// <summary>
    /// Implemented by types that carry an explicit ordering position.
    /// </summary>
    public interface IOrdered
    {
        /// <summary>
        /// The relative sort position of the instance; lower values come first.
        /// </summary>
        int Order { get; }
    }
}
