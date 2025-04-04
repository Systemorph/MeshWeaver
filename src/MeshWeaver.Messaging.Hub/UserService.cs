namespace MeshWeaver.Messaging;

public class UserService
{
    private readonly AsyncLocal<UserContext> context = new();

    public UserContext Context => context.Value;
    public void SetContext(UserContext userContext)
    {
        context.Value = userContext;
    }
}

public record UserContext
{
    public string UserId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public IReadOnlyCollection<string> Roles { get; init; } = [];
}
