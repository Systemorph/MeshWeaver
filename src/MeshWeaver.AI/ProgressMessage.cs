#nullable enable
using MeshWeaver.Domain;

namespace MeshWeaver.AI
{
    /// <summary>
    /// Represents a progress message for AI operations
    /// </summary>
    public record ProgressMessage
    {
        /// <summary>
        /// Icon for the progress message  
        /// </summary>
        public Icon? Icon { get; init; }
        /// <summary>
        /// Progress message text
        /// </summary>
        public required string Message { get; init; }
    }
}
