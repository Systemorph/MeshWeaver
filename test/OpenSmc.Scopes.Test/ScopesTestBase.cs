using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Application.Scope;
using OpenSmc.Fixture;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using OpenSmc.Scopes.Proxy;
using OpenSmc.ServiceProvider;
using Xunit.Abstractions;

namespace OpenSmc.Scopes.Test;

public class ScopesTestBase : HubTestBase
{

    protected IScopeFactory ScopeFactory;

    protected ScopesTestBase(ITestOutputHelper output)
        : base(output)
    {
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddApplicationScope();
    }

    public override void Initialize()
    {
        base.Initialize();
        ScopeFactory = GetHost().ServiceProvider.GetRequiredService<IScopeFactory>();
    }

    protected (TScope scope, IdentitiesStorage storage) GetSingleTestScope<TScope>()
        where TScope : class, IScope
    {
        var (scopes, storage) = GetTestScopes<TScope>();
        return (scopes.First(), storage);
    }

    protected virtual (ICollection<TScope> scopes, IdentitiesStorage storage) GetTestScopes<TScope>(int nScopes = 1)
        where TScope : class, IScope
    {
        var storage = new IdentitiesStorage(nScopes);
        var scopes = ScopeFactory.ForIdentities(storage.Identities, storage)
            .ToScopes<TScope>();
        return (scopes, storage);
    }
}
