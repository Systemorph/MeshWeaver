using System.Dynamic;
using AspectCore.Extensions.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Scopes;
using OpenSmc.Scopes.Proxy;
using OpenSmc.Serialization;
using OpenSmc.ShortGuid;

namespace OpenSmc.Application.Scope;

public static class ApplicationScopeRegistryExtensions
{
    public static MessageHubConfiguration AddApplicationScope(this MessageHubConfiguration conf, Func<ApplicationScopeConfiguration, ApplicationScopeConfiguration> applicationScopeConfig = null)
    {
        var applicationScopeAddress = new ApplicationScopeAddress(conf.Address);
        return conf
            .WithServices(services => services
                .RegisterScopes()
                .AddSingleton<IScopeFactory, ScopeFactory>()
                .AddSingleton(serviceProvider => serviceProvider.GetRequiredService<IScopeFactory>().ForSingleton()
                    .ToScope<IApplicationScope>()))
            .AddSerialization(rule =>
                rule.ForType<IScope>(typeBuilder => typeBuilder.WithTransformation(TransformScope)))

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

public class ApplicationScopeConfiguration
{
    public ApplicationScopeConfiguration WithTypesFromAssembly<T>() => this; // T will be interface type, inherited from IMutableScope
    public ApplicationScopeConfiguration WithType<T>() => this;
}