
using System.CommandLine;
using MeshWeaver.ThumbnailGenerator;

var catalogUrlOption = new Option<string>(name: "--catalogUrl", description: "Full URL to the LayoutArea catalog page");
var outDirOption = new Option<string>(name: "--output", description: "Output directory for thumbnails", getDefaultValue: () => Path.Combine(Environment.CurrentDirectory, "thumbnails"));

var root = new RootCommand("Generates thumbnails for layout areas (scaffold)");
root.AddOption(catalogUrlOption);
root.AddOption(outDirOption);

root.SetHandler(async (catalogUrl, output) =>
{
    if (string.IsNullOrWhiteSpace(catalogUrl))
    {
        Console.WriteLine("Error: --catalogUrl is required.");
        return;
    }
    Directory.CreateDirectory(output);
    Console.WriteLine($"[ThumbnailGenerator] Fetching catalog: {catalogUrl}");

    List<string> areaUrls = new();
    try
    {
        areaUrls = await AreaUrlExtractor.GetAreaUrlsAsync(catalogUrl);

        Console.WriteLine($"Found {areaUrls.Count} area URLs:");
        foreach (var url in areaUrls)
            Console.WriteLine("  " + url);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error fetching or parsing catalog: {ex.Message}");
        return;
    }

    if (!areaUrls.Any())
    {
        Console.WriteLine("No area URLs found to screenshot.");
        return;
    }

    // Generate thumbnails for each area URL using the same domain (to inherit localStorage)
    Console.WriteLine($"\nGenerating thumbnails for {areaUrls.Count} areas...");
    var catalogUri = new Uri(catalogUrl);
    var baseUrl = $"{catalogUri.Scheme}://{catalogUri.Authority}";
    await ThumbnailGenerator.GenerateThumbnailsAsync(areaUrls, output, baseUrl);
}, catalogUrlOption, outDirOption);

return await root.InvokeAsync(args);
