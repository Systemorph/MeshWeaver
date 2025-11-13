using System.CommandLine;
using MeshWeaver.ThumbnailGenerator;

var catalogUrlOption = new Option<string?>("--catalogUrl")
{
    Description = "Full URL to the LayoutArea catalog page"
};

var singleAreaOption = new Option<string?>("--area")
{
    Description = "URL of a single area to screenshot (alternative to --catalogUrl)"
};

var outDirOption = new Option<string>("--output")
{
    DefaultValueFactory = _ => Path.Combine(Environment.CurrentDirectory, "thumbnails"),
    Description = "Output directory for thumbnails"
};

var darkModeOption = new Option<bool>("--dark-mode")
{
    DefaultValueFactory = _ => true,
    Description = "Generate dark mode thumbnails in addition to light mode"
};

var thumbnailWidthOption = new Option<int>("--width")
{
    DefaultValueFactory = _ => 400,
    Description = "Thumbnail width in pixels"
};

var thumbnailHeightOption = new Option<int>("--height")
{
    DefaultValueFactory = _ => 300,
    Description = "Thumbnail height in pixels"
};

var root = new RootCommand("Generates thumbnails for layout areas (scaffold)")
{
    catalogUrlOption,
    singleAreaOption,
    outDirOption,
    darkModeOption,
    thumbnailWidthOption,
    thumbnailHeightOption
};

root.SetAction(async (parseResult, cancellationToken) =>
{
    var catalogUrl = parseResult.GetValue(catalogUrlOption);
    var area = parseResult.GetValue(singleAreaOption);
    var output = parseResult.GetValue(outDirOption);
    var darkMode = parseResult.GetValue(darkModeOption);
    var width = parseResult.GetValue(thumbnailWidthOption);
    var height = parseResult.GetValue(thumbnailHeightOption);

    // Validate input - either catalogUrl or area must be provided
    if (string.IsNullOrWhiteSpace(catalogUrl) && string.IsNullOrWhiteSpace(area))
    {
        Console.WriteLine("Error: Either --catalogUrl or --area is required.");
        return 1;
    }

    if (!string.IsNullOrWhiteSpace(catalogUrl) && !string.IsNullOrWhiteSpace(area))
    {
        Console.WriteLine("Error: Cannot specify both --catalogUrl and --area. Use one or the other.");
        return 1;
    }

    Directory.CreateDirectory(output!);

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
            areaUrls = await AreaUrlExtractor.GetAreaUrlsAsync(catalogUrl!);

            Console.WriteLine($"Found {areaUrls.Count} area URLs:");
            foreach (var url in areaUrls)
                Console.WriteLine("  " + url);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching or parsing catalog: {ex.Message}");
            return 1;
        }

        if (!areaUrls.Any())
        {
            Console.WriteLine("No area URLs found to screenshot.");
            return 1;
        }

        var catalogUri = new Uri(catalogUrl!);
        baseUrl = $"{catalogUri.Scheme}://{catalogUri.Authority}";
    }

    // Generate thumbnails
    if (darkMode)
    {
        Console.WriteLine($"\nGenerating light and dark mode thumbnails for {areaUrls.Count} area(s) at {width}x{height}...");
        await ThumbnailGenerator.GenerateThumbnailsAsync(areaUrls, output!, baseUrl, true, width, height);
    }
    else
    {
        Console.WriteLine($"\nGenerating light mode thumbnails for {areaUrls.Count} area(s) at {width}x{height}...");
        await ThumbnailGenerator.GenerateThumbnailsAsync(areaUrls, output!, baseUrl, false, width, height);
    }

    return 0;
});

return root.Parse(args).Invoke();
