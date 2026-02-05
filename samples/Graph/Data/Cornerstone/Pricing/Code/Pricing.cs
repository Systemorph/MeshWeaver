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
    public string InsuredName { get; init; } = string.Empty;

    /// <summary>
    /// Brief description of the pricing.
    /// </summary>
    [Markdown(EditorHeight = "150px", ShowPreview = false)]
    [MeshNodeProperty(nameof(MeshNode.Description))]
    public string? Description { get; init; }

    /// <summary>
    /// The date when the insurance coverage begins.
    /// </summary>
    [DisplayName("Inception Date")]
    [UiControl(Style = "width: 180px;")]
    public DateTime? InceptionDate { get; init; }

    /// <summary>
    /// The date when the insurance coverage ends.
    /// </summary>
    [DisplayName("Expiration Date")]
    [UiControl(Style = "width: 180px;")]
    public DateTime? ExpirationDate { get; init; }

    /// <summary>
    /// The underwriting year for this pricing.
    /// </summary>
    [DisplayName("Underwriting Year")]
    [UiControl(Style = "width: 120px;")]
    public int? UnderwritingYear { get; init; }

    /// <summary>
    /// Line of business classification (dimension).
    /// </summary>
    [Dimension<LineOfBusiness>]
    [DisplayName("Line of Business")]
    [UiControl(Style = "width: 200px;")]
    public string? LineOfBusiness { get; init; }

    /// <summary>
    /// Country code or name (dimension).
    /// </summary>
    [Dimension<Country>]
    [UiControl(Style = "width: 200px;")]
    public string? Country { get; init; }

    /// <summary>
    /// Name of the broker handling this pricing.
    /// </summary>
    [DisplayName("Broker")]
    [UiControl(Style = "width: 200px;")]
    public string? BrokerName { get; init; }

    /// <summary>
    /// Name of the primary insurance company.
    /// </summary>
    [DisplayName("Primary Insurance")]
    [UiControl(Style = "width: 200px;")]
    public string? PrimaryInsurance { get; init; }

    /// <summary>
    /// Currency code for the premium.
    /// </summary>
    [Dimension<Currency>]
    [UiControl(Style = "width: 120px;")]
    public string? Currency { get; init; }

    /// <summary>
    /// Legal entity underwriting this pricing.
    /// </summary>
    [Dimension<LegalEntity>]
    [DisplayName("Legal Entity")]
    [UiControl(Style = "width: 200px;")]
    public string? LegalEntity { get; init; }

    /// <summary>
    /// Current status of the pricing.
    /// </summary>
    [Dimension<PricingStatus>]
    [UiControl(Style = "width: 150px;")]
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
