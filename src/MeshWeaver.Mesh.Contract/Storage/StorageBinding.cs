using System.ComponentModel;

namespace MeshWeaver.Mesh.Storage;

/// <summary>
/// The content of a <c>StorageBinding</c> node at <c>{partition}/_Storage/{id}</c>: WHERE one
/// purpose of one partition is stored, when it is not where the instance would put it anyway.
///
/// <para><b>A binding only overrides.</b> With no binding, every purpose resolves to the
/// instance's own pre-configured store (<see cref="IStorageBindingResolver"/>) — its Postgres, in
/// the partition's own schema; its content storage. A binding names a container in one of the
/// instance's stores (<see cref="StoreId"/> + <see cref="Container"/>), picked from the store's live
/// list or created there with the instance's identity, and it overrides only once its
/// <see cref="ValidationStatus"/> reads <see cref="StorageValidationStatus.Valid"/>.</para>
///
/// <para><b>Never a secret in content.</b> A completely separate account is the advanced option and is
/// carried as a VAULT REFERENCE only — a Key Vault secret NAME (<see cref="VaultSecretName"/>) or a
/// managed-identity client id — never a key, a password or a connection string.</para>
///
/// <para>The validation fields are written by the binding's OWN hub (the watcher that answers
/// <see cref="RequestedAction"/> and every change of target), not by the editor, which is why they
/// are not browsable.</para>
/// </summary>
public record StorageBinding
{
    /// <summary>What this location is for (<see cref="StoragePurpose"/>).</summary>
    [Description("Purpose")]
    public string Purpose { get; init; } = StoragePurpose.DocParts;

    /// <summary>The pre-configured store the container lives in (<see cref="IInstanceStore.Id"/>).
    /// Empty: the store the instance uses by default for <see cref="Purpose"/>.</summary>
    [Description("Store")]
    public string? StoreId { get; init; }

    /// <summary>The container in that store — a Postgres schema, a blob container, a directory.</summary>
    [Description("Container / schema")]
    public string? Container { get; init; }

    /// <summary>A prefix for the tables this purpose creates in a schema, where the store has tables.</summary>
    [Description("Table prefix")]
    public string? TablePrefix { get; init; }

    /// <summary>The content collection (content repo) this binding serves; empty: the whole partition.</summary>
    [Description("Collection")]
    public string? Collection { get; init; }

    /// <summary>The partition's default binding for <see cref="Purpose"/> — the one used for any
    /// collection no other binding names.</summary>
    [Description("Default for this purpose")]
    public bool IsDefault { get; init; } = true;

    /// <summary>The name of a NEW container to create in the store (input to <see cref="StorageBindingAction.Create"/>).</summary>
    [Description("New container name")]
    public string? NewContainerName { get; init; }

    /// <summary>ADVANCED — a separate account: the Key Vault secret NAME that holds its connection.
    /// Never the secret itself.</summary>
    [Description("Separate account: vault secret name")]
    public string? VaultSecretName { get; init; }

    /// <summary>ADVANCED — a separate account reached with a managed identity: that identity's client id.</summary>
    [Description("Separate account: managed identity client id")]
    public string? ManagedIdentityClientId { get; init; }

    /// <summary>ADVANCED — a separate account's endpoint (host or URL), never carrying credentials.</summary>
    [Description("Separate account: endpoint")]
    public string? ExternalEndpoint { get; init; }

    /// <summary>What the viewer asked the binding's hub to do (<see cref="StorageBindingAction"/>);
    /// cleared by the hub when it has acted.</summary>
    [Browsable(false)]
    public string? RequestedAction { get; init; }

    /// <summary>When <see cref="RequestedAction"/> was asked — two asks of the same action are two asks.</summary>
    [Browsable(false)]
    public DateTimeOffset? RequestedAt { get; init; }

    /// <summary>The verdict of the last validation (<see cref="StorageValidationStatus"/>).</summary>
    [Browsable(false)]
    public string ValidationStatus { get; init; } = StorageValidationStatus.Pending;

    /// <summary>Why — the store's own words on a failure, what was checked on a success.</summary>
    [Browsable(false)]
    public string? ValidationMessage { get; init; }

    /// <summary>When the verdict was recorded (UTC).</summary>
    [Browsable(false)]
    public DateTimeOffset? ValidatedAt { get; init; }

    /// <summary>The target the verdict is about (<see cref="TargetKey"/> at validation time). A binding
    /// whose target moved since is re-validated, and is not <see cref="IsUsable"/> until it is.</summary>
    [Browsable(false)]
    public string? ValidatedTarget { get; init; }

    /// <summary>
    /// The identity of what this binding points AT — every field a verdict depends on. Pure.
    /// </summary>
    public string TargetKey()
        => string.Join('|', Purpose, StoreId ?? "", Container ?? "", TablePrefix ?? "",
            VaultSecretName ?? "", ManagedIdentityClientId ?? "", ExternalEndpoint ?? "");

    /// <summary>True when the binding names a separate account rather than a pre-configured store.</summary>
    public bool IsSeparateAccount()
        => !string.IsNullOrWhiteSpace(VaultSecretName) || !string.IsNullOrWhiteSpace(ManagedIdentityClientId);

    /// <summary>
    /// True when this binding may override the default: validated <see cref="StorageValidationStatus.Valid"/>
    /// FOR ITS CURRENT TARGET. An edit that moved the target makes it unusable until the hub has
    /// validated the new one — never the old verdict carried over to a new container.
    /// </summary>
    public bool IsUsable()
        => string.Equals(ValidationStatus, StorageValidationStatus.Valid, StringComparison.Ordinal)
           && string.Equals(ValidatedTarget, TargetKey(), StringComparison.Ordinal)
           && !string.IsNullOrWhiteSpace(Container);
}
