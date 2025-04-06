namespace MeshWeaver.Messaging;

public class UserService
{
    private readonly AsyncLocal<AccessContext> context = new();

    public AccessContext Context => context.Value;
    public void SetContext(AccessContext accessContext)
    {
        context.Value = accessContext;
    }
}
