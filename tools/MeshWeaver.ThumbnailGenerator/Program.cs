
using System.CommandLine;
using MeshWeaver.ThumbnailGenerator;

var catalogUrlOption = new Option<string>(name: "--catalogUrl", description: "Full URL to the LayoutArea catalog page");
var singleAreaOption = new Option<string>(name: "--area", description: "URL of a single area to screenshot (alternative to --catalogUrl)");
var outDirOption = new Option<string>(name: "--output", description: "Output directory for thumbnails", getDefaultValue: () => Path.Combine(Environment.CurrentDirectory, "thumbnails"));
var darkModeOption = new Option<bool>(name: "--dark-mode", description: "Generate dark mode thumbnails in addition to light mode", getDefaultValue: () => true);

var root = new RootCommand("Generates thumbnails for layout areas (scaffold)");
root.AddOption(catalogUrlOption);
root.AddOption(singleAreaOption);
root.AddOption(outDirOption);
root.AddOption(darkModeOption);

root.SetHandler(async (catalogUrl, singleArea, output, includeDarkMode) =>
{
    // Validate input - either catalogUrl or singleArea must be provided
    if (string.IsNullOrWhiteSpace(catalogUrl) && string.IsNullOrWhiteSpace(singleArea))
    {
        Console.WriteLine("Error: Either --catalogUrl or --area is required.");
        return;
    }
    
    if (!string.IsNullOrWhiteSpace(catalogUrl) && !string.IsNullOrWhiteSpace(singleArea))
    {
        Console.WriteLine("Error: Cannot specify both --catalogUrl and --area. Use one or the other.");
        return;
    }

    Directory.CreateDirectory(output);

    List<string> areaUrls = new();
    string baseUrl;

    if (!string.IsNullOrWhiteSpace(singleArea))
    {
        // Single area mode
        Console.WriteLine($"[ThumbnailGenerator] Single area mode: {singleArea}");
        areaUrls.Add(singleArea);
        
        var singleAreaUri = new Uri(singleArea);
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
    if (includeDarkMode)
    {
        Console.WriteLine($"\nGenerating light and dark mode thumbnails for {areaUrls.Count} area(s)...");
        await ThumbnailGenerator.GenerateThumbnailsAsync(areaUrls, output, baseUrl, true);
    }
    else
    {
        Console.WriteLine($"\nGenerating light mode thumbnails for {areaUrls.Count} area(s)...");
        await ThumbnailGenerator.GenerateThumbnailsAsync(areaUrls, output, baseUrl, false);
    }
}, catalogUrlOption, singleAreaOption, outDirOption, darkModeOption);

return await root.InvokeAsync(args);
