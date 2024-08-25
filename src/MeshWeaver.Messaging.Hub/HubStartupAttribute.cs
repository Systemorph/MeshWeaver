namespace MeshWeaver.Messaging;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public abstract class HubStartupAttribute(string id) : Attribute
{
    public string Id { get; } = id;

    public abstract IMessageHub Create(IServiceProvider serviceProvider, object address);
}
