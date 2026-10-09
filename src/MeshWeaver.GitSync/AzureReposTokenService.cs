using System.Reactive.Linq;
using Azure.Core;
using Azure.Identity;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.GitSync;

/// <summary>
/// Mints the Entra access token an Azure Repos push authenticates with (MeshWeaver#5248): the
/// instance's <b>workload identity</b>, exchanged through a federated credential on the app
/// registration DECLARED in <see cref="AzureDevOpsOptions"/> (the client's tenant + client id from
/// the instance's deployment record), for the Azure DevOps scope
/// <see cref="AzureDevOpsOptions.Scope"/>. Secretless: no PAT and no client secret exist anywhere.
///
/// <para>The exchange itself is <see cref="WorkloadIdentityCredential"/> — the SDK's own
/// implementation, which reads the projected service-account token
/// (<c>AZURE_FEDERATED_TOKEN_FILE</c>) and caches the access token until shortly before it
/// expires. The token call is an IO leaf, so it runs inside <see cref="IIoPool"/>
/// (<see cref="IoPoolNames.Http"/>); the surface is a cold <see cref="IObservable{T}"/>.</para>
/// </summary>
public sealed class AzureReposTokenService
{
    private readonly IoPoolRegistry ioPools;
    private readonly AzureDevOpsOptions options;
    private readonly TokenCredential? credential;
    private readonly ILogger<AzureReposTokenService>? logger;

    /// <summary>Initializes the service from the declared options.</summary>
    /// <param name="ioPools">The mesh's IO pools.</param>
    /// <param name="options">The declared Azure DevOps identity.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="credentialFactory">Builds the credential from the declared tenant + client id;
    /// defaults to <see cref="WorkloadIdentityCredential"/>. Tests pass their own.</param>
    public AzureReposTokenService(
        IoPoolRegistry ioPools,
        IOptions<AzureDevOpsOptions> options,
        ILogger<AzureReposTokenService>? logger = null,
        Func<AzureDevOpsOptions, TokenCredential>? credentialFactory = null)
    {
        this.ioPools = ioPools;
        this.options = options.Value;
        this.logger = logger;
        if (this.options.IsConfigured)
            credential = (credentialFactory ?? DefaultCredential)(this.options);
    }

    /// <summary>True when the tenant + client id are declared.</summary>
    public bool IsConfigured => credential is not null;

    /// <summary>
    /// The current access token for Azure DevOps. Errors, naming the two keys, when the identity is
    /// not declared on this instance.
    /// </summary>
    public IObservable<string> GetToken()
    {
        if (credential is null)
            return Observable.Throw<string>(new InvalidOperationException(
                "Azure Repos is not configured on this instance: declare AzureDevOps:TenantId and " +
                "AzureDevOps:ClientId (the client's tenant and the app registration federated to this " +
                "instance's workload identity) in its deployment record."));
        var context = new TokenRequestContext([AzureDevOpsOptions.Scope], tenantId: options.TenantId);
        return ioPools.Get(IoPoolNames.Http)
            .Invoke(ct => credential.GetTokenAsync(context, ct).AsTask())
            .Do(_ => { }, ex => logger?.LogWarning(ex,
                "Azure DevOps token for tenant {Tenant} / client {Client} could not be minted.",
                options.TenantId, options.ClientId))
            .Select(token => token.Token);
    }

    private static TokenCredential DefaultCredential(AzureDevOpsOptions declared)
        => new WorkloadIdentityCredential(new WorkloadIdentityCredentialOptions
        {
            TenantId = declared.TenantId,
            ClientId = declared.ClientId,
        });
}
