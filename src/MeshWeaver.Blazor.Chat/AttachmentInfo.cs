namespace MeshWeaver.Blazor.Chat;

/// <summary>
/// Represents an attachment (context or reference) in the chat input area.
/// </summary>
/// <param name="Path">The mesh path of the attachment.</param>
/// <param name="DisplayName">Optional display name; falls back to last path segment.</param>
/// <param name="IsContext">True if this attachment originated from the navigation context.</param>
public record AttachmentInfo(string Path, string? DisplayName, bool IsContext);
