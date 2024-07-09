using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Application.Styles;
using OpenSmc.Data.Serialization;
using OpenSmc.Domain;
using OpenSmc.Messaging.Serialization;
using OpenSmc.Utils;

namespace OpenSmc.Data;

public abstract record TypeSource<TTypeSource> : ITypeSource
    where TTypeSource : TypeSource<TTypeSource>
{
    protected TypeSource(IWorkspace workspace, Type ElementType, object DataSource)
    {
        this.ElementType = ElementType;
        this.DataSource = DataSource;
        Workspace = workspace;
        var typeRegistry = Workspace
            .Hub.ServiceProvider.GetRequiredService<ITypeRegistry>()
            .WithType(ElementType);
        CollectionName = typeRegistry.TryGetTypeName(ElementType, out var typeName)
            ? typeName
            : ElementType.FullName;

        typeRegistry.WithType(ElementType);
        Key = typeRegistry.GetKeyFunction(CollectionName);
        var displayAttribute = ElementType.GetCustomAttribute<DisplayAttribute>();
        DisplayName = displayAttribute?.GetName() ?? ElementType.Name.Wordify();
        var xmlCommentsMethod = ElementType.Assembly.GetType($"{ElementType.Assembly.GetName().Name}.CodeComments")
            ?.GetMethod("GetSummary", BindingFlags.Public | BindingFlags.Static);

        var getFromXmlComment = xmlCommentsMethod == null ? null : (Func<string, string>) (x => xmlCommentsMethod.Invoke(null, new object[] { x })?.ToString());
        Description = GetDescription(ElementType, displayAttribute, getFromXmlComment);
        MemberDescriptions = ElementType.GetMembers()
            .Select(x => new KeyValuePair<string,string>(x.Name, GetFromXmlComments(x, getFromXmlComment)))
            .DistinctBy(x => x.Key)
            .ToDictionary();
        GroupName = displayAttribute?.GetGroupName();
        Order = displayAttribute?.GetOrder();
        var iconAttribute = ElementType.GetCustomAttribute<IconAttribute>();
        if (iconAttribute != null)
            Icon = new Icon(iconAttribute.Provider, iconAttribute.Id);
    }

    private Dictionary<string, string> MemberDescriptions { get;  }
    public string GetDescription(string memberName) => MemberDescriptions.GetValueOrDefault(memberName, "Add description in the xml comments or in the display attribute");
    private string GetDescription(Type elementType, DisplayAttribute displayAttribute,
        Func<string, string> fromXmlComment)
    {
        return displayAttribute?.GetDescription()
               ?? fromXmlComment?.Invoke($"T:{elementType.FullName}")
               ?? "Add description in the xml comments or in the display attribute";
    }

    private string GetFromXmlComments(MemberInfo member, Func<string, string> getMemberComment)
    {
        if (getMemberComment == null)
            return null;
        return member switch
        {
            Type type => getMemberComment($"T:{type.FullName}"),
            PropertyInfo => getMemberComment($"P:{member.ReflectedType?.FullName}.{member.Name}"),
            MethodInfo => getMemberComment($"M:{member.ReflectedType?.FullName}.{member.Name}"),
            FieldInfo => getMemberComment($"F:{member.ReflectedType?.FullName}.{member.Name}"),
            _ => null
        };
    }





    public string Description { get; init; }
    public string GroupName { get; init; }
    public int? Order { get; init; }


    public virtual object GetKey(object instance) =>
        Key.Function?.Invoke(instance)
        ?? throw new DataSourceConfigurationException(
            "No key mapping is defined. Please specify in the configuration of the data sources source.");

    protected KeyFunction Key { get; init; }

    protected TTypeSource This => (TTypeSource)this;
    protected IWorkspace Workspace { get; }

    public virtual InstanceCollection Update(ChangeItem<EntityStore> workspace)
    {
        var myCollection = workspace.Value.Reduce(new CollectionReference(CollectionName));

        return UpdateImpl(myCollection);
    }

    private IDisposable workspaceSubscription;

    protected virtual InstanceCollection UpdateImpl(InstanceCollection myCollection) =>
        myCollection;

    ITypeSource ITypeSource.WithInitialData(
        Func<
            WorkspaceReference<InstanceCollection>,
            CancellationToken,
            Task<IEnumerable<object>>
        > initialization
    ) => WithInitialData(initialization);

    public TTypeSource WithInitialData(
        Func<
            WorkspaceReference<InstanceCollection>,
            CancellationToken,
            Task<IEnumerable<object>>
        > initialization
    ) => This with { InitializationFunction = initialization };

    protected Func<
        WorkspaceReference<InstanceCollection>,
        CancellationToken,
        Task<IEnumerable<object>>
    > InitializationFunction { get; init; } = (_, _) => Task.FromResult(Enumerable.Empty<object>());

    public Type ElementType { get; init; }
    public string DisplayName { get; init; }
    public object DataSource { get; init; }
    public string CollectionName { get; init; }
    public object Icon { get; init; }

    Task<InstanceCollection> ITypeSource.InitializeAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken cancellationToken
    ) => InitializeAsync(reference, cancellationToken);

    protected virtual async Task<InstanceCollection> InitializeAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken cancellationToken
    )
    {
        return new InstanceCollection(
            await InitializeDataAsync(reference, cancellationToken),
            GetKey
        );
    }

    private Task<IEnumerable<object>> InitializeDataAsync(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken cancellationToken
    ) => InitializationFunction(reference, cancellationToken);

    public void Dispose()
    {
        workspaceSubscription?.Dispose();
    }
}
