namespace MeshWeaver.Mesh;

/// <summary>
/// Configuration for the inbound/outbound Microsoft Teams bot channel (bidirectional: a Teams user
/// messages the bot, a Memex agent answers in the same chat). Disabled by default — the bot endpoint and
/// hosted services self-skip unless <see cref="Enabled"/> and the Bot credentials are set, so the feature
/// ships inert until an admin provisions an Azure Bot resource + Teams app and turns it on.
/// </summary>
public sealed class TeamsOptions
{
    public const string SectionName = "Teams";

    /// <summary>Master switch. False = the Teams bot endpoint + reply sender do nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>The Azure Bot / app registration id (Bot Framework "MicrosoftAppId").</summary>
    public string? AppId { get; set; }

    /// <summary>The Bot app client secret ("MicrosoftAppPassword"). Keep in Key Vault.</summary>
    public string? AppPassword { get; set; }

    /// <summary>Entra tenant id for a single-tenant bot (optional; multi-tenant when empty).</summary>
    public string? TenantId { get; set; }
}
