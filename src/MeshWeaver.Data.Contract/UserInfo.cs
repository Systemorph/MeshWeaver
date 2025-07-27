#nullable enable
namespace MeshWeaver.Data;

public record UserInfo(string Email, string DisplayName, string? Photo = default);