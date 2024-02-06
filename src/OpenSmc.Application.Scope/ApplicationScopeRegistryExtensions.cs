using System.Collections.Immutable;
using System.Dynamic;
using System.Reflection;
using AspectCore.Extensions.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Reflection;
using OpenSmc.Scopes;
using OpenSmc.Scopes.Proxy;
using OpenSmc.Serialization;
using OpenSmc.ShortGuid;

namespace OpenSmc.Application.Scope;

public static class ApplicationScopeRegistryExtensions
{
    public static MessageHubConfiguration AddApplicationScope(this MessageHubConfiguration conf, Func<ApplicationScopeConfiguration, ApplicationScopeConfiguration> configureApplicationScope = null)
    {
        var applicationScopeConfig = configureApplicationScope?.Invoke(new ApplicationScopeConfiguration()) ?? new ApplicationScopeConfiguration();
        var applicationScopeAddress = new ApplicationScopeAddress(conf.Address);
        return conf
            .WithServices(services => services
                .RegisterScopes()
                .AddSingleton<IScopeFactory, ScopeFactory>()
                .AddSingleton(serviceProvider => serviceProvider.GetRequiredService<IScopeFactory>().ForSingleton()
                    .ToScope<IApplicationScope>()))
            .AddSerialization(rule =>
                rule.ForType<IScope>(typeBuilder => typeBuilder.WithTransformation(TransformScope))
                    .ApplyScopeDeserialization(applicationScopeConfig))

            .WithHostedHub
            (
                new ApplicationScopeAddress(conf.Address),
                d => d.AddPlugin<ApplicationScopePlugin>())
            .WithRoutes
            (
                forward => forward
                    .RouteMessage<ScopePropertyChanged>(
                        d => d.Message.Status switch
                        {
                            PropertyChangeStatus.Requested => applicationScopeAddress,
                            _ => MessageTargets.Subscribers
                        })
                    .RouteMessage<ScopePropertyChangedEvent>(
                        d => d.Message.Status switch
                        {
                            ScopeChangedStatus.Requested => applicationScopeAddress,
                            _ => MessageTargets.Subscribers
                        })
            );

    }

    static readonly MethodInfo GetScopeMethod = ReflectionHelper.GetMethodGeneric<IApplicationScope>(x => x.GetScope<object>());
    private static SerializationConfiguration ApplyScopeDeserialization(this SerializationConfiguration conf, ApplicationScopeConfiguration scopeConfiguration)
    {
        foreach(var type in scopeConfiguration.Types)
        {
            conf = conf.WithTypeFactory(type, sp =>
            {
                var appScope = sp.GetRequiredService<IApplicationScope>();                
                return GetScopeMethod.MakeGenericMethod(type).InvokeAsFunction(appScope); // todo Identity
        });
        }
        return conf;
    }

    public const string ScopeId = "$scopeId";
    public const string TypeDiscriminator = "$type";

    private static object TransformScope(IScope scope, ISerializationTransformContext context)
    {
        var scopeType = scope.GetScopeType();
        var properties = scopeType.GetScopeProperties().SelectMany(x => x.Properties);

        IDictionary<string, object> ret = new ExpandoObject();

        ret[ScopeId] = scope.GetGuid().AsString();
        ret[TypeDiscriminator] = scopeType.Name;


        foreach (var property in properties)
        {
            ret[property.Name] = context.TraverseProperty(property.GetReflector().GetValue(scope), scope, property);
        }

        return ret;
    }

}

public record ApplicationScopeConfiguration
{
    internal ImmutableHashSet<Type> Types = ImmutableHashSet<Type>.Empty;
    public ApplicationScopeConfiguration WithTypesFromAssembly<T>() => this; // T will be interface type, inherited from IMutableScope
    public ApplicationScopeConfiguration WithType<T>() => this with { Types = Types.Add(typeof(T)) };
}