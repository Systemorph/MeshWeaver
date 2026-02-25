// <meshweaver>
// Id: MicrosoftDataLoader
// DisplayName: Microsoft Data Loader
// </meshweaver>

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Messaging;

/// <summary>
/// Async loader utility for Microsoft pricing data.
/// Loads PropertyRisks from JSON and reinsurance structure from Slip.md.
/// Reads files directly from the file system to avoid timing issues with content service initialization.
/// </summary>
public static class MicrosoftDataLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Loads PropertyRisk records from PropertyRisks.json in the Submissions folder.
    /// </summary>
    /// <param name="hub">The message hub for resolving paths</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of PropertyRisk records</returns>
    public static async Task<PropertyRisk[]> LoadPropertyRisksAsync(
        IMessageHub hub,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var filePath = GetPropertyRisksFilePath(hub);

            if (!File.Exists(filePath))
                return Array.Empty<PropertyRisk>();

            using var stream = File.OpenRead(filePath);
            var risks = await JsonSerializer.DeserializeAsync<PropertyRisk[]>(stream, JsonOptions, cancellationToken);
            return risks ?? Array.Empty<PropertyRisk>();
        }
        catch
        {
            return Array.Empty<PropertyRisk>();
        }
    }

    /// <summary>
    /// Gets the file system path for PropertyRisks.json.
    /// Tries multiple locations to support both production and test environments.
    /// </summary>
    /// <param name="hub">The message hub</param>
    /// <returns>Absolute path to the PropertyRisks.json file</returns>
    private static string GetPropertyRisksFilePath(IMessageHub hub)
    {
        var insuredName = hub.Address.Segments[2];
        var pricingYear = hub.Address.Segments[3];

        // Try production path first (relative to current working directory)
        var productionPath = Path.GetFullPath(Path.Combine(
            "../../samples/Graph/Data/ACME/Insurance",
            insuredName,
            pricingYear,
            "PropertyRisks.json"
        ));

        if (File.Exists(productionPath))
            return productionPath;

        // Try test path (SamplesGraph copied to bin directory)
        var testPath = Path.Combine(
            AppContext.BaseDirectory,
            "SamplesGraph",
            "Data",
            "ACME",
            "Insurance",
            insuredName,
            pricingYear,
            "PropertyRisks.json"
        );

        if (File.Exists(testPath))
            return testPath;

        // Fallback: return production path (will fail gracefully with empty data)
        return productionPath;
    }

    /// <summary>
    /// Loads reinsurance structure from Slip.md in the Submissions folder.
    /// </summary>
    /// <param name="hub">The message hub for resolving paths</param>
    /// <param name="pricingId">The pricing ID for generated records</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple of acceptances and sections</returns>
    public static async Task<(ReinsuranceAcceptance[] Acceptances, ReinsuranceSection[] Sections)> LoadReinsuranceStructureAsync(
        IMessageHub hub,
        string pricingId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var basePath = GetSubmissionsBasePath(hub);
            var filePath = Path.Combine(basePath, "Slip.md");

            if (!File.Exists(filePath))
                return (Array.Empty<ReinsuranceAcceptance>(), Array.Empty<ReinsuranceSection>());

            var markdown = await File.ReadAllTextAsync(filePath, cancellationToken);
            return SlipParser.ParseSlip(markdown, pricingId);
        }
        catch
        {
            return (Array.Empty<ReinsuranceAcceptance>(), Array.Empty<ReinsuranceSection>());
        }
    }

    /// <summary>
    /// Gets the file system base path for Submissions folder.
    /// Tries multiple locations to support both production and test environments.
    /// </summary>
    /// <param name="hub">The message hub</param>
    /// <returns>Absolute path to the Submissions folder</returns>
    private static string GetSubmissionsBasePath(IMessageHub hub)
    {
        var insuredName = hub.Address.Segments[2];
        var pricingYear = hub.Address.Segments[3];
        var submissionsSubPath = Path.Combine("content", "ACME", "Insurance", insuredName, pricingYear, "Submissions");

        // Try production path first (relative to current working directory)
        var productionPath = Path.GetFullPath(Path.Combine(
            "../../samples/Graph/content/ACME/Insurance",
            insuredName,
            pricingYear,
            "Submissions"
        ));

        if (Directory.Exists(productionPath))
            return productionPath;

        // Try test path (SamplesGraph copied to bin directory)
        var testPath = Path.Combine(AppContext.BaseDirectory, "SamplesGraph", submissionsSubPath);
        if (Directory.Exists(testPath))
            return testPath;

        // Fallback: return production path (will fail gracefully with empty data)
        return productionPath;
    }
}
