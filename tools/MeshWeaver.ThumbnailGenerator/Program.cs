using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using MeshWeaver.ThumbnailGenerator;

var catalogUrlOption = new Option<string>(
    name: "--catalogUrl",
    description: "Full URL to the LayoutArea catalog page");

var singleAreaOption = new Option<string>(
    name: "--area",
    description: "URL of a single area to screenshot (alternative to --catalogUrl)");

var outDirOption = new Option<string>(
    name: "--output",
    getDefaultValue: () => Path.Combine(Environment.CurrentDirectory, "thumbnails"),
    description: "Output directory for thumbnails");

var darkModeOption = new Option<bool>(
    name: "--dark-mode",
    getDefaultValue: () => true,
    description: "Generate dark mode thumbnails in addition to light mode");

var thumbnailWidthOption = new Option<int>(
    name: "--width",
    getDefaultValue: () => 400,
    description: "Thumbnail width in pixels");

var thumbnailHeightOption = new Option<int>(
    name: "--height",
    getDefaultValue: () => 300,
    description: "Thumbnail height in pixels");

var root = new RootCommand("Generates thumbnails for layout areas (scaffold)");
root.AddOption(catalogUrlOption);
root.AddOption(singleAreaOption);
root.AddOption(outDirOption);
root.AddOption(darkModeOption);
root.AddOption(thumbnailWidthOption);
root.AddOption(thumbnailHeightOption);

root.Handler = CommandHandler.Create<string, string, string, bool, int, int>(async (catalogUrl, area, output, darkMode, width, height) =>
{
    // Validate input - either catalogUrl or area must be provided
    if (string.IsNullOrWhiteSpace(catalogUrl) && string.IsNullOrWhiteSpace(area))
    {
        Console.WriteLine("Error: Either --catalogUrl or --area is required.");
        return;
    }

    if (!string.IsNullOrWhiteSpace(catalogUrl) && !string.IsNullOrWhiteSpace(area))
    {
        Console.WriteLine("Error: Cannot specify both --catalogUrl and --area. Use one or the other.");
        return;
    }

    Directory.CreateDirectory(output);

    List<string> areaUrls = new();
    string baseUrl;

    if (!string.IsNullOrWhiteSpace(area))
    {
        // Single area mode
        Console.WriteLine($"[ThumbnailGenerator] Single area mode: {area}");
        areaUrls.Add(area);

        var singleAreaUri = new Uri(area);
        baseUrl = $"{singleAreaUri.Scheme}://{singleAreaUri.Authority}";
    }
    else
    {
        // Catalog mode
        Console.WriteLine($"[ThumbnailGenerator] Catalog mode: {catalogUrl}");

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

        var catalogUri = new Uri(catalogUrl);
        baseUrl = $"{catalogUri.Scheme}://{catalogUri.Authority}";
    }

    // Generate thumbnails
    if (darkMode)
    {
        Console.WriteLine($"\nGenerating light and dark mode thumbnails for {areaUrls.Count} area(s) at {width}x{height}...");
        await ThumbnailGenerator.GenerateThumbnailsAsync(areaUrls, output, baseUrl, true, width, height);
    }
    else
    {
        Console.WriteLine($"\nGenerating light mode thumbnails for {areaUrls.Count} area(s) at {width}x{height}...");
        await ThumbnailGenerator.GenerateThumbnailsAsync(areaUrls, output, baseUrl, false, width, height);
    }
});

return await root.InvokeAsync(args);