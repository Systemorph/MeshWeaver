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
/// (<see cref="FederatedTokenFileVariable"/>) and caches the access token until shortly before it
/// expires. The token call is an IO leaf, so it runs inside <see cref="IIoPool"/>
/// (<see cref="IoPoolNames.Http"/>); the surface is a cold <see cref="IObservable{T}"/>.</para>
///
/// <para>🚨 <b>Prerequisite: the pod must carry a projected workload-identity token.</b> The AKS
/// workload-identity webhook projects it only into a pod labelled
/// <c>azure.workload.identity/use: "true"</c>, which the chart sets when the deployment record
/// declares <c>selfUpdate.azureClientId</c>. Without it there is no token to exchange, so
/// <see cref="GetToken"/> refuses by name rather than handing back the SDK's generic error.</para>
/// </summary>
public sealed class AzureReposTokenService
{
    /// <summary>The environment variable the workload-identity webhook projects the service-account
    /// token path into.</summary>
    public const string FederatedTokenFileVariable = "AZURE_FEDERATED_TOKEN_FILE";

    private readonly IoPoolRegistry ioPools;
    private readonly AzureDevOpsOptions options;
    private readonly TokenCredential? credential;
    private readonly ILogger<AzureReposTokenService>? logger;
    private readonly Func<string, string?> environment;

    /// <summary>Initializes the service from the declared options.</summary>
    /// <param name="ioPools">The mesh's IO pools.</param>
    /// <param name="options">The declared Azure DevOps identity.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="credentialFactory">Builds the credential from the declared tenant + client id;
    /// defaults to <see cref="WorkloadIdentityCredential"/>. Tests pass their own.</param>
    /// <param name="environment">Reads an environment variable; defaults to the process environment.
    /// Tests pass their own.</param>
    public AzureReposTokenService(
        IoPoolRegistry ioPools,
        IOptions<AzureDevOpsOptions> options,
        ILogger<AzureReposTokenService>? logger = null,
        Func<AzureDevOpsOptions, TokenCredential>? credentialFactory = null,
        Func<string, string?>? environment = null)
    {
        this.ioPools = ioPools;
        this.options = options.Value;
        this.logger = logger;
        this.environment = environment ?? Environment.GetEnvironmentVariable;
        if (this.options.IsConfigured)
            credential = (credentialFactory ?? DefaultCredential)(this.options);
    }

    /// <summary>True when the tenant + client id are declared.</summary>
    public bool IsConfigured => credential is not null;

    /// <summary>
    /// The current access token for Azure DevOps. Refuses, by name and in the viewer's language
    /// (<see cref="GitSyncRefusal"/>), when the identity is not declared on this instance, and when
    /// the pod carries no projected workload-identity token.
    /// </summary>
    public IObservable<string> GetToken()
    {
        if (credential is null)
            return Observable.Throw<string>(GitSyncRefusal.Of("gitsync.azure.identityNotDeclared").ToException());
        if (string.IsNullOrWhiteSpace(environment(FederatedTokenFileVariable)))
            return Observable.Throw<string>(GitSyncRefusal.Of("gitsync.azure.noWorkloadIdentity",
                ("variable", FederatedTokenFileVariable)).ToException());
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
