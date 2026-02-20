// <meshweaver>
// Id: MicrosoftSampleData
// DisplayName: Microsoft Sample Data
// </meshweaver>

using MeshWeaver.Import.Configuration;

/// <summary>
/// Sample data configuration for Microsoft/2026 pricing demonstration.
/// Property risks and reinsurance structure are loaded from source documents
/// (PropertyRisks.json and Slip.md) via MicrosoftDataLoader.
/// </summary>
public static class MicrosoftSampleData
{
    public const string PricingId = "2026";

    /// <summary>
    /// Excel import configuration for Microsoft.xlsx.
    /// </summary>
    public static readonly ExcelImportConfiguration[] ImportConfigs =
    [
        new()
        {
            Name = "Microsoft.xlsx",
            Address = "pricing/Microsoft/2026",
            TypeName = nameof(PropertyRisk),
            DataStartRow = 7,
            TotalRowMarkers = ["Total", "Grand Total"],
            TotalRowScanAllCells = true,
            TotalRowMatchExact = false,
            Mappings =
            [
                new() { TargetProperty = "Id", Kind = MappingKind.Direct, SourceColumns = ["C"] },
                new() { TargetProperty = "LocationName", Kind = MappingKind.Direct, SourceColumns = ["D"] },
                new() { TargetProperty = "PricingId", Kind = MappingKind.Constant, ConstantValue = PricingId },
                new() { TargetProperty = "Address", Kind = MappingKind.Direct, SourceColumns = ["E"] },
                new() { TargetProperty = "Country", Kind = MappingKind.Direct, SourceColumns = ["B"] },
                new() { TargetProperty = "TsiBuilding", Kind = MappingKind.Direct, SourceColumns = ["H"] },
                new() { TargetProperty = "TsiContent", Kind = MappingKind.Sum, SourceColumns = ["G", "I", "J", "K", "L", "M", "N", "O", "P"] }
            ],
            Allocations = [new() { TargetProperty = "TsiBi", WeightColumns = ["Q"] }],
            IgnoreRowExpressions = ["Id == null", "Address == null"]
        }
    ];
}
