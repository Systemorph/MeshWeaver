// <meshweaver>
// Id: SlipParser
// DisplayName: Slip Markdown Parser
// </meshweaver>

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

/// <summary>
/// Parser utility to extract reinsurance structure from Slip.md markdown files.
/// Extracts EPI, Brokerage, and layer/section definitions from structured markdown.
/// </summary>
public static class SlipParser
{
    // Pre-compiled regex patterns
    private static readonly Regex EpiPattern = new(@"Estimated Premium Income \(EPI\):\s*USD\s*([\d,]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BrokeragePattern = new(@"Brokerage:\s*(\d+)%", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CoverageSectionPattern = new(@"###\s*\d+\.\s*([^\n]+)\n(.*?)(?=###|\z)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex LayerPattern = new(@"-\s*\*\*Layer\s*(\d+):\*\*\s*(.*?)(?=-\s*\*\*Layer|\z)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex DeductiblePattern = new(@"Deductible per Occurrence:\s*USD\s*([\d,]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AttachmentPattern = new(@"Attachment Point:\s*USD\s*([\d,]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LimitPattern = new(@"Limit per Occurrence:\s*USD\s*([\d,]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AggregateDeductiblePattern = new(@"Annual Aggregate Deductible:\*\*\s*USD\s*([\d,]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AggregateLimitPattern = new(@"Annual Aggregate Limit:\*\*\s*USD\s*([\d,]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses a Slip.md file and extracts reinsurance acceptances and sections.
    /// </summary>
    /// <param name="markdown">The markdown content of the slip file</param>
    /// <param name="pricingId">The pricing ID to assign to generated records</param>
    /// <returns>Tuple of acceptances and sections extracted from the slip</returns>
    public static (ReinsuranceAcceptance[] Acceptances, ReinsuranceSection[] Sections) ParseSlip(
        string markdown,
        string pricingId)
    {
        var epi = ParseEpi(markdown);
        var brokerage = ParseBrokerage(markdown);

        var coverages = ParseCoverages(markdown);

        var acceptances = GenerateAcceptances(pricingId, epi, brokerage);
        var sections = GenerateSections(pricingId, coverages);

        return (acceptances, sections);
    }

    /// <summary>
    /// Parses the Estimated Premium Income (EPI) from the markdown.
    /// </summary>
    private static decimal ParseEpi(string markdown)
    {
        var match = EpiPattern.Match(markdown);
        if (match.Success && decimal.TryParse(
                match.Groups[1].Value.Replace(",", ""),
                out var epi))
        {
            return epi;
        }
        return 200_000_000m; // Default from Microsoft slip
    }

    /// <summary>
    /// Parses the Brokerage percentage from the markdown.
    /// </summary>
    private static double ParseBrokerage(string markdown)
    {
        var match = BrokeragePattern.Match(markdown);
        if (match.Success && double.TryParse(match.Groups[1].Value, out var brokerage))
        {
            return brokerage / 100.0;
        }
        return 0.10; // Default 10%
    }

    /// <summary>
    /// Parses all coverage sections from the markdown.
    /// </summary>
    private static List<CoverageDefinition> ParseCoverages(string markdown)
    {
        var coverages = new List<CoverageDefinition>();

        // Extract each coverage section
        var coverageMatches = CoverageSectionPattern.Matches(markdown);

        foreach (Match coverageMatch in coverageMatches)
        {
            var coverageName = coverageMatch.Groups[1].Value.Trim();
            var coverageContent = coverageMatch.Groups[2].Value;

            var coverage = new CoverageDefinition
            {
                Name = coverageName,
                Layers = ParseLayers(coverageContent)
            };

            coverages.Add(coverage);
        }

        // If no coverages found, use defaults based on Slip.md structure
        if (coverages.Count == 0)
        {
            coverages = GetDefaultCoverages();
        }

        return coverages;
    }

    /// <summary>
    /// Parses layer definitions from a coverage section.
    /// </summary>
    private static List<LayerDefinition> ParseLayers(string content)
    {
        var layers = new List<LayerDefinition>();

        // Match Layer 1, Layer 2, Layer 3 sections
        var layerMatches = LayerPattern.Matches(content);

        foreach (Match layerMatch in layerMatches)
        {
            var layerNumber = int.Parse(layerMatch.Groups[1].Value);
            var layerContent = layerMatch.Groups[2].Value;

            var layer = new LayerDefinition
            {
                Number = layerNumber,
                Deductible = ParseAmount(layerContent, DeductiblePattern),
                Attachment = ParseAmount(layerContent, AttachmentPattern),
                Limit = ParseAmount(layerContent, LimitPattern),
                AggregateDeductible = ParseAmount(layerContent, AggregateDeductiblePattern),
                AggregateLimit = ParseAmount(layerContent, AggregateLimitPattern)
            };

            // For Layer 1, use deductible as attachment if no explicit attachment
            if (layer.Number == 1 && layer.Attachment == 0 && layer.Deductible > 0)
            {
                layer.Attachment = layer.Deductible;
            }

            layers.Add(layer);
        }

        // If no layers found, use defaults
        if (layers.Count == 0)
        {
            layers = GetDefaultLayers();
        }

        return layers;
    }

    /// <summary>
    /// Parses a USD amount from a regex match.
    /// </summary>
    private static decimal ParseAmount(string content, Regex regex)
    {
        var match = regex.Match(content);
        if (match.Success && decimal.TryParse(
                match.Groups[1].Value.Replace(",", ""),
                out var amount))
        {
            return amount;
        }
        return 0;
    }

    /// <summary>
    /// Generates ReinsuranceAcceptance records from parsed data.
    /// </summary>
    private static ReinsuranceAcceptance[] GenerateAcceptances(
        string pricingId,
        decimal totalEpi,
        double brokerage)
    {
        var epiPerLayer = totalEpi / 3;
        var rate = 0.00033; // Standard rate

        return new[]
        {
            new ReinsuranceAcceptance
            {
                Id = "L1",
                PricingId = pricingId,
                Name = "Layer 1 - Primary",
                EPI = (double)epiPerLayer,
                Rate = rate,
                Brokerage = brokerage,
                Commission = 0.05
            },
            new ReinsuranceAcceptance
            {
                Id = "L2",
                PricingId = pricingId,
                Name = "Layer 2 - First Excess",
                EPI = (double)epiPerLayer,
                Rate = rate,
                Brokerage = brokerage,
                Commission = 0.05
            },
            new ReinsuranceAcceptance
            {
                Id = "L3",
                PricingId = pricingId,
                Name = "Layer 3 - Second Excess",
                EPI = (double)epiPerLayer,
                Rate = rate,
                Brokerage = brokerage,
                Commission = 0.05
            }
        };
    }

    /// <summary>
    /// Generates ReinsuranceSection records from parsed coverages.
    /// </summary>
    private static ReinsuranceSection[] GenerateSections(
        string pricingId,
        List<CoverageDefinition> coverages)
    {
        var sections = new List<ReinsuranceSection>();
        var coverageAbbreviations = new Dictionary<string, string>
        {
            ["Fire Damage"] = "FIRE",
            ["Natural Catastrophe"] = "NAT",
            ["Natural Catastrophe (Windstorm, Earthquake)"] = "NAT",
            ["Business Interruption"] = "BI"
        };

        foreach (var coverage in coverages)
        {
            var abbrev = coverageAbbreviations.GetValueOrDefault(coverage.Name, "UNK");

            foreach (var layer in coverage.Layers)
            {
                var acceptanceId = $"L{layer.Number}";

                sections.Add(new ReinsuranceSection
                {
                    Id = $"{acceptanceId}-{abbrev}",
                    AcceptanceId = acceptanceId,
                    Name = $"{coverage.Name} - Layer {layer.Number}",
                    LineOfBusiness = "PROP",
                    Attach = layer.Attachment,
                    Limit = layer.Limit,
                    AggAttach = layer.AggregateDeductible > 0 ? layer.AggregateDeductible : (decimal?)null,
                    AggLimit = layer.AggregateLimit > 0 ? layer.AggregateLimit : (decimal?)null
                });
            }
        }

        return sections.ToArray();
    }

    /// <summary>
    /// Returns default coverages based on Microsoft Slip.md structure.
    /// </summary>
    private static List<CoverageDefinition> GetDefaultCoverages()
    {
        var defaultLayers = GetDefaultLayers();
        return new List<CoverageDefinition>
        {
            new CoverageDefinition { Name = "Fire Damage", Layers = defaultLayers },
            new CoverageDefinition { Name = "Natural Catastrophe", Layers = defaultLayers },
            new CoverageDefinition { Name = "Business Interruption", Layers = defaultLayers }
        };
    }

    /// <summary>
    /// Returns default layers based on Microsoft Slip.md structure.
    /// </summary>
    private static List<LayerDefinition> GetDefaultLayers()
    {
        return new List<LayerDefinition>
        {
            new LayerDefinition
            {
                Number = 1,
                Attachment = 5_000_000m,
                Limit = 100_000_000m,
                AggregateDeductible = 25_000_000m,
                AggregateLimit = 300_000_000m
            },
            new LayerDefinition
            {
                Number = 2,
                Attachment = 105_000_000m,
                Limit = 145_000_000m,
                AggregateDeductible = 0,
                AggregateLimit = 435_000_000m
            },
            new LayerDefinition
            {
                Number = 3,
                Attachment = 250_000_000m,
                Limit = 250_000_000m,
                AggregateDeductible = 0,
                AggregateLimit = 750_000_000m
            }
        };
    }
}

/// <summary>
/// Internal model for coverage definitions during parsing.
/// </summary>
internal class CoverageDefinition
{
    public string Name { get; set; } = string.Empty;
    public List<LayerDefinition> Layers { get; set; } = new List<LayerDefinition>();
}

/// <summary>
/// Internal model for layer definitions during parsing.
/// </summary>
internal class LayerDefinition
{
    public int Number { get; set; }
    public decimal Deductible { get; set; }
    public decimal Attachment { get; set; }
    public decimal Limit { get; set; }
    public decimal AggregateDeductible { get; set; }
    public decimal AggregateLimit { get; set; }
}
