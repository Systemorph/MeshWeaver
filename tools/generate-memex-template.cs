#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable

using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: dotnet run generate-memex-template.cs -- <version> <repoRoot> [outputPath]");
    return 1;
}

var version = args[0];
var repoRoot = Path.GetFullPath(args[1]);
var outputPath = Path.GetFullPath(args.Length > 2
    ? args[2]
    : Path.Combine(repoRoot, "dist", "templates"));

Console.WriteLine($"Generating Memex template v{version}");
Console.WriteLine($"  source : {Path.Combine(repoRoot, "memex")}");
Console.WriteLine($"  output : {outputPath}");

var projectsToCopy = new (string Src, string Dest)[]
{
    ("memex/Memex.Portal.Monolith",               "Memex.Portal.Monolith"),
    ("memex/Memex.Portal.Shared",                 "Memex.Portal.Shared"),
    ("memex/aspire/Memex.AppHost",                "aspire/Memex.AppHost"),
    ("memex/aspire/Memex.Database.Migration",     "aspire/Memex.Database.Migration"),
    ("memex/aspire/Memex.Portal.Distributed",     "aspire/Memex.Portal.Distributed"),
    ("memex/aspire/Memex.Portal.ServiceDefaults", "aspire/Memex.Portal.ServiceDefaults"),
};

var excludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "bin", "obj", ".vs", ".idea", "Azurite" };
var excludedFileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { ".user" };

if (Directory.Exists(outputPath))
    Directory.Delete(outputPath, recursive: true);
Directory.CreateDirectory(outputPath);

foreach (var (src, dest) in projectsToCopy)
{
    var srcAbs = Path.Combine(repoRoot, src);
    if (!Directory.Exists(srcAbs))
        throw new DirectoryNotFoundException($"Source project not found: {srcAbs}");
    CopyDirectory(srcAbs, Path.Combine(outputPath, dest));
}

// --- Copy sample data (users + ACME) for Developer Login and sample org ---
var excludedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "Roland", "Samuel", "Roland.json", "Samuel.json", "Roland_Access.json", "Samuel_Access.json" };

CopySampleData(
    Path.Combine(repoRoot, "samples/Graph/Data/User"),
    Path.Combine(outputPath, "samples/Graph/Data/User"),
    excludedNames);
CopySampleData(
    Path.Combine(repoRoot, "samples/Graph/Data/ACME"),
    Path.Combine(outputPath, "samples/Graph/Data/ACME"),
    excludedNames);

// Fix relative data paths in appsettings.Development.json (memex/ is 2 levels deep, template is 1).
// Only rewrite inside "BasePath": "..." values to avoid touching comments or unrelated strings.
foreach (var appSettings in Directory.EnumerateFiles(outputPath, "appsettings.Development.json", SearchOption.AllDirectories))
{
    var text = File.ReadAllText(appSettings);
    var rewritten = Regex.Replace(text,
        @"(""BasePath""\s*:\s*"")\.\.\/\.\.\/(samples\/Graph)",
        "$1../$2");
    if (text != rewritten)
        File.WriteAllText(appSettings, rewritten);
}

var rootVersions = LoadPackageVersions(Path.Combine(repoRoot, "Directory.Packages.props"));

foreach (var csproj in Directory.EnumerateFiles(outputPath, "*.csproj", SearchOption.AllDirectories))
    RewriteCsproj(csproj, rootVersions);

var packageRefs = CollectPackageRefs(outputPath);
WriteDirectoryPackagesProps(
    Path.Combine(outputPath, "Directory.Packages.props"),
    packageRefs,
    version,
    rootVersions);

WriteFile(Path.Combine(outputPath, "nuget.config"),                       Resources.NugetConfig);
WriteFile(Path.Combine(outputPath, "Directory.Build.props"),              Resources.DirectoryBuildProps);
WriteFile(Path.Combine(outputPath, "Memex.slnx"),                         Resources.MemexSlnx);
WriteFile(Path.Combine(outputPath, "README.md"),                          Resources.Readme);
WriteFile(Path.Combine(outputPath, ".template.config", "template.json"),  Resources.TemplateJson);

var generated = Directory.GetFiles(outputPath, "*", SearchOption.AllDirectories).Length;
Console.WriteLine($"Generated {generated} files in {outputPath}");
return 0;

void CopySampleData(string src, string dest, HashSet<string> excluded)
{
    if (!Directory.Exists(src)) return;
    Directory.CreateDirectory(dest);
    foreach (var dir in Directory.EnumerateDirectories(src))
    {
        var name = Path.GetFileName(dir);
        if (excluded.Contains(name)) continue;
        CopySampleData(dir, Path.Combine(dest, name), excluded);
    }
    foreach (var file in Directory.EnumerateFiles(src))
    {
        var name = Path.GetFileName(file);
        if (excluded.Contains(name)) continue;
        File.Copy(file, Path.Combine(dest, name), overwrite: true);
    }
}

void CopyDirectory(string src, string dest)
{
    Directory.CreateDirectory(dest);
    foreach (var dir in Directory.EnumerateDirectories(src))
    {
        if (excludedDirs.Contains(Path.GetFileName(dir))) continue;
        CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }
    foreach (var file in Directory.EnumerateFiles(src))
    {
        if (excludedFileExtensions.Contains(Path.GetExtension(file))) continue;
        File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
    }
}

void RewriteCsproj(string csprojPath, Dictionary<string, string> rootVersions)
{
    var doc = XDocument.Load(csprojPath, LoadOptions.PreserveWhitespace);
    var changed = false;

    foreach (var pr in doc.Descendants("ProjectReference").ToList())
    {
        var include = pr.Attribute("Include")?.Value;
        if (string.IsNullOrEmpty(include)) continue;

        var normalized = include.Replace('\\', '/');

        if (Regex.IsMatch(normalized, @"[/\\]src[/\\]MeshWeaver\.[\w.]+[/\\]", RegexOptions.IgnoreCase))
        {
            var pkgName = Path.GetFileNameWithoutExtension(normalized);
            pr.ReplaceWith(new XElement("PackageReference", new XAttribute("Include", pkgName)));
            changed = true;
        }
        else if (Regex.IsMatch(normalized, @"[/\\]samples[/\\]", RegexOptions.IgnoreCase))
        {
            pr.Remove();
            changed = true;
        }
    }

    // Keep MSBuild SDK refs in lockstep with their NuGet counterparts in Directory.Packages.props.
    // Aspire.AppHost.Sdk has no corresponding PackageReference, so it would silently drift otherwise.
    var sdkSync = new (string SdkName, string PackageKey)[]
    {
        ("Aspire.AppHost.Sdk", "Aspire.Hosting.AppHost"),
    };
    foreach (var (sdkName, pkgKey) in sdkSync)
    {
        if (!rootVersions.TryGetValue(pkgKey, out var ver)) continue;
        foreach (var sdk in doc.Descendants("Sdk")
                     .Where(e => string.Equals(e.Attribute("Name")?.Value, sdkName, StringComparison.OrdinalIgnoreCase)))
        {
            if (sdk.Attribute("Version")?.Value != ver)
            {
                sdk.SetAttributeValue("Version", ver);
                changed = true;
            }
        }
    }

    if (changed)
        doc.Save(csprojPath);
}

static Dictionary<string, string> LoadPackageVersions(string path)
{
    var doc = XDocument.Load(path);
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var pv in doc.Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
    {
        var include = pv.Attribute("Include")?.Value;
        var ver = pv.Attribute("Version")?.Value;
        if (!string.IsNullOrEmpty(include) && !string.IsNullOrEmpty(ver))
            result[include] = ver;
    }
    return result;
}

static SortedSet<string> CollectPackageRefs(string root)
{
    var refs = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var csproj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
    {
        var doc = XDocument.Load(csproj);
        foreach (var pr in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
        {
            var include = pr.Attribute("Include")?.Value;
            if (!string.IsNullOrEmpty(include))
                refs.Add(include);
        }
    }
    return refs;
}

static void WriteDirectoryPackagesProps(
    string path,
    SortedSet<string> refs,
    string version,
    Dictionary<string, string> rootVersions)
{
    var sb = new StringBuilder();
    sb.AppendLine("<Project>");
    sb.AppendLine("  <PropertyGroup>");
    sb.AppendLine("    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>");
    sb.AppendLine("  </PropertyGroup>");
    sb.AppendLine("  <ItemGroup>");
    sb.AppendLine("    <!-- MeshWeaver packages -->");
    foreach (var r in refs.Where(x => x.StartsWith("MeshWeaver.", StringComparison.Ordinal)))
        sb.AppendLine($"    <PackageVersion Include=\"{r}\" Version=\"{version}\" />");

    sb.AppendLine("    <!-- Third-party packages -->");
    var missing = new List<string>();
    foreach (var r in refs.Where(x => !x.StartsWith("MeshWeaver.", StringComparison.Ordinal)))
    {
        if (rootVersions.TryGetValue(r, out var v))
            sb.AppendLine($"    <PackageVersion Include=\"{r}\" Version=\"{v}\" />");
        else
            missing.Add(r);
    }
    sb.AppendLine("  </ItemGroup>");
    sb.AppendLine("</Project>");

    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, sb.ToString());

    if (missing.Count > 0)
    {
        Console.Error.WriteLine($"WARNING: {missing.Count} package(s) missing from root Directory.Packages.props:");
        foreach (var m in missing)
            Console.Error.WriteLine($"  - {m}");
    }
}

static void WriteFile(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
}

static class Resources
{
    public const string NugetConfig = """
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
            <add key="github" value="https://nuget.pkg.github.com/Systemorph/index.json" protocolVersion="3" />
            <add key="local" value="../packages" />
          </packageSources>
          <packageSourceMapping>
            <packageSource key="nuget.org">
              <package pattern="*" />
            </packageSource>
            <packageSource key="github">
              <package pattern="MeshWeaver.*" />
            </packageSource>
            <packageSource key="local">
              <package pattern="MeshWeaver.*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """;

    public const string DirectoryBuildProps = """
        <Project>
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <LangVersion>latest</LangVersion>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    public const string MemexSlnx = """
        <Solution>
          <Project Path="Memex.Portal.Monolith/Memex.Portal.Monolith.csproj" />
          <Project Path="Memex.Portal.Shared/Memex.Portal.Shared.csproj" />
          <Folder Name="/aspire/">
            <Project Path="aspire/Memex.AppHost/Memex.AppHost.csproj" />
            <Project Path="aspire/Memex.Portal.Distributed/Memex.Portal.Distributed.csproj" />
            <Project Path="aspire/Memex.Database.Migration/Memex.Database.Migration.csproj" />
            <Project Path="aspire/Memex.Portal.ServiceDefaults/Memex.Portal.ServiceDefaults.csproj" />
          </Folder>
        </Solution>
        """;

    public const string Readme = """
        # Memex Portal

        A MeshWeaver portal application with Graph support, AI integration, and sample ACME data.

        ## Projects

        | Project | Description |
        |---------|-------------|
        | `Memex.Portal.Monolith` | Blazor Server portal (development, file-system storage) |
        | `Memex.Portal.Shared` | Shared Razor class library (auth, config, UI) |
        | `aspire/Memex.AppHost` | .NET Aspire orchestrator (distributed deployment) |
        | `aspire/Memex.Portal.Distributed` | Orleans co-hosted portal (PostgreSQL, Azure) |
        | `aspire/Memex.Database.Migration` | PostgreSQL schema migration worker |
        | `aspire/Memex.Portal.ServiceDefaults` | Shared Aspire services (telemetry, health checks) |

        ## Getting Started

        ### Monolith (Recommended for Development)

        ```bash
        dotnet run --project Memex.Portal.Monolith
        ```

        Access at **https://localhost:7122**. Uses file-system storage with ACME sample data.

        ### Microservices (.NET Aspire)

        ```bash
        dotnet run --project aspire/Memex.AppHost
        ```

        Requires Docker for PostgreSQL and Azure Storage emulation.
        """;

    public const string TemplateJson = """
        {
          "$schema": "http://json.schemastore.org/template",
          "author": "Systemorph",
          "classifications": [ "Solution", "MeshWeaver", "Memex", "Web" ],
          "name": "MeshWeaver Memex Solution",
          "identity": "MeshWeaver.Memex.CSharp",
          "groupIdentity": "MeshWeaver.Memex",
          "shortName": "meshweaver-memex",
          "tags": {
            "language": "C#",
            "type": "solution"
          },
          "sourceName": "Memex",
          "preferNameDirectory": true,
          "symbols": {
            "framework": {
              "type": "parameter",
              "description": "The target framework for the project.",
              "datatype": "choice",
              "choices": [
                { "choice": "net10.0", "description": ".NET 10.0" }
              ],
              "defaultValue": "net10.0",
              "replaces": "net10.0"
            }
          },
          "sources": [
            {
              "modifiers": [
                {
                  "exclude": [
                    "**/bin/**",
                    "**/obj/**",
                    "**/.vs/**",
                    "**/.idea/**",
                    "**/Azurite/**"
                  ]
                }
              ]
            }
          ],
          "primaryOutputs": [
            { "path": "Memex.Portal.Monolith/Memex.Portal.Monolith.csproj" },
            { "path": "Memex.Portal.Shared/Memex.Portal.Shared.csproj" },
            { "path": "aspire/Memex.AppHost/Memex.AppHost.csproj" },
            { "path": "aspire/Memex.Portal.Distributed/Memex.Portal.Distributed.csproj" },
            { "path": "aspire/Memex.Database.Migration/Memex.Database.Migration.csproj" },
            { "path": "aspire/Memex.Portal.ServiceDefaults/Memex.Portal.ServiceDefaults.csproj" }
          ],
          "postActions": [
            {
              "description": "Restore NuGet packages required by this project.",
              "manualInstructions": [
                { "text": "Run 'dotnet restore'" }
              ],
              "actionId": "210D431B-A78B-4D2F-B762-4ED3E3EA9025",
              "continueOnError": true
            }
          ]
        }
        """;
}
