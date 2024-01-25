using AspectCore.Extensions.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Hub;
using OpenSmc.Scopes;
using OpenSmc.Scopes.Proxy;
using OpenSmc.Serialization;
using OpenSmc.ShortGuid;
using static System.Formats.Asn1.AsnWriter;
using System.Dynamic;

namespace OpenSmc.Application.Scope;

public class ApplicationAddressOptions
{
    public ApplicationAddress Address { get; set; }
}

public record ApplicationVariable(IApplicationScope Scope) // todo delete Scope
{
    public IMessageHub Host { get; private set; }
    public ApplicationAddress Address { get; private set; }

    public void Initialize(IMessageHub host, ApplicationAddress applicationAddress)
    {
        Host = host;
        Address = applicationAddress;
    }
}

public static class ApplicationScopeRegistryExtensions
{
    public static MessageHubConfiguration ConfigureApplication(this MessageHubConfiguration conf, ApplicationAddress address)
        => conf.WithServices(services => services.AddSingleton(serviceProvider => new ApplicationVariable(serviceProvider.GetRequiredService<IApplicationScope>())))
               .WithBuildupAction(hub =>
                                      hub.ServiceProvider.GetRequiredService<ApplicationVariable>().Initialize(hub, address));


    public static MessageHubConfiguration AddExpressionSynchronization(this MessageHubConfiguration conf)
    {
        return conf.AddPlugin<ExpressionSynchronizationPlugin>();
    }


    public static MessageHubConfiguration AddApplicationScope(this MessageHubConfiguration conf)
    {
        return conf.AddPlugin<ApplicationScopePlugin>()
                   .WithServices(s => s.AddSingleton(serviceProvider => serviceProvider.GetRequiredService<IScopeFactory>().ForSingleton().ToScope<IApplicationScope>()))
                   .AddSerialization(rule => rule.ForType<IScope>(typeBuilder => typeBuilder.WithTransformation(TransformScope)));
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