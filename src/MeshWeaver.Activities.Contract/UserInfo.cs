#nullable enable
namespace MeshWeaver.Activities;

public record UserInfo(string Email, string DisplayName, string? Photo = default);