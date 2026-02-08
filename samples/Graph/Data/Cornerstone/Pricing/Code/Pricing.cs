// <meshweaver>
// Id: Pricing
// DisplayName: Cornerstone Pricing Data Model
// </meshweaver>

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;


/// <summary>
/// Represents an insurance pricing entity with dimension-based classification.
/// </summary>
public record Pricing : IContentInitializable
{
    /// <summary>
    /// Unique identifier for the pricing record.
    /// </summary>
    [Key]
    [Browsable(false)]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// The name of the insured party - used as the MeshNode name.
    /// </summary>
    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    [UiControl(Style = "width: 100%;")]
    [Description("The full legal name of the insured company or individual")]
    public string InsuredName { get; init; } = string.Empty;

    /// <summary>
    /// Brief description of the pricing.
    /// </summary>
    [Markdown(EditorHeight = "150px", ShowPreview = false)]
    [MeshNodeProperty(nameof(MeshNode.Description))]
    [Description("Detailed description of the pricing, coverage scope, and special conditions")]
    public string? Description { get; init; }

    /// <summary>
    /// The date when the insurance coverage begins.
    /// </summary>
    [DisplayName("Inception Date")]
    [UiControl(Style = "width: 180px;")]
    [Description("The effective start date of the insurance coverage")]
    public DateTime? InceptionDate { get; init; }

    /// <summary>
    /// The date when the insurance coverage ends.
    /// </summary>
    [DisplayName("Expiration Date")]
    [UiControl(Style = "width: 180px;")]
    [Description("The end date of the insurance coverage period")]
    public DateTime? ExpirationDate { get; init; }

    /// <summary>
    /// The underwriting year for this pricing.
    /// </summary>
    [DisplayName("Underwriting Year")]
    [UiControl(Style = "width: 120px;")]
    [Description("The fiscal year for underwriting purposes (auto-filled from inception date if not set)")]
    [Range(2000, 2100, ErrorMessage = "Underwriting Year must be between 2000 and 2100")]
    public int? UnderwritingYear { get; init; }

    /// <summary>
    /// Line of business classification (dimension).
    /// </summary>
    [Dimension<LineOfBusiness>]
    [DisplayName("Line of Business")]
    [UiControl(Style = "width: 200px;")]
    [Description("The insurance line of business category (Property, Casualty, Marine, etc.)")]
    public string? LineOfBusiness { get; init; }

    /// <summary>
    /// Country code or name (dimension).
    /// </summary>
    [Dimension<Country>]
    [UiControl(Style = "width: 200px;")]
    [Description("The primary country where the insured risk is located")]
    public string? Country { get; init; }

    /// <summary>
    /// Name of the broker handling this pricing.
    /// </summary>
    [DisplayName("Broker")]
    [UiControl(Style = "width: 200px;")]
    [Description("The insurance broker or intermediary managing this placement")]
    public string? BrokerName { get; init; }

    /// <summary>
    /// Name of the primary insurance company.
    /// </summary>
    [DisplayName("Primary Insurance")]
    [UiControl(Style = "width: 200px;")]
    [Description("The primary insurer providing direct coverage to the insured")]
    public string? PrimaryInsurance { get; init; }

    /// <summary>
    /// Currency code for the premium.
    /// </summary>
    [Dimension<Currency>]
    [UiControl(Style = "width: 120px;")]
    [Description("The currency used for premium and limit calculations")]
    public string? Currency { get; init; }

    /// <summary>
    /// Legal entity underwriting this pricing.
    /// </summary>
    [Dimension<LegalEntity>]
    [DisplayName("Legal Entity")]
    [UiControl(Style = "width: 200px;")]
    [Description("The Cornerstone legal entity that will underwrite this risk")]
    public string? LegalEntity { get; init; }

    /// <summary>
    /// Current status of the pricing.
    /// </summary>
    [Dimension<PricingStatus>]
    [UiControl(Style = "width: 150px;")]
    [Description("The current workflow status of the pricing (Draft, Quoted, Bound, etc.)")]
    public string Status { get; init; } = "Draft";

    /// <summary>
    /// Initializes the Pricing record.
    /// </summary>
    public object Initialize()
    {
        // Set default underwriting year if not provided
        if (!UnderwritingYear.HasValue && InceptionDate.HasValue)
        {
            return this with { UnderwritingYear = InceptionDate.Value.Year };
        }
        return this;
    }
}
