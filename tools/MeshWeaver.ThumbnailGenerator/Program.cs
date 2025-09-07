
using System.CommandLine;
using MeshWeaver.ThumbnailGenerator;

var catalogUrlOption = new Option<string>(name: "--catalogUrl", description: "Full URL to the LayoutArea catalog page");
var outDirOption = new Option<string>(name: "--output", description: "Output directory for thumbnails", getDefaultValue: () => Path.Combine(Environment.CurrentDirectory, "thumbnails"));
var darkModeOption = new Option<bool>(name: "--dark-mode", description: "Generate dark mode thumbnails in addition to light mode", getDefaultValue: () => true);

var root = new RootCommand("Generates thumbnails for layout areas (scaffold)");
root.AddOption(catalogUrlOption);
root.AddOption(outDirOption);
root.AddOption(darkModeOption);

root.SetHandler(async (catalogUrl, output, includeDarkMode) =>
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
    var catalogUri = new Uri(catalogUrl);
    var baseUrl = $"{catalogUri.Scheme}://{catalogUri.Authority}";
    
    if (includeDarkMode)
    {
        Console.WriteLine($"\nGenerating light and dark mode thumbnails for {areaUrls.Count} areas...");
        await ThumbnailGenerator.GenerateThumbnailsAsync(areaUrls, output, baseUrl, true);
    }
    else
    {
        Console.WriteLine($"\nGenerating light mode thumbnails for {areaUrls.Count} areas...");
        await ThumbnailGenerator.GenerateThumbnailsAsync(areaUrls, output, baseUrl, false);
    }
}, catalogUrlOption, outDirOption, darkModeOption);

return await root.InvokeAsync(args);
