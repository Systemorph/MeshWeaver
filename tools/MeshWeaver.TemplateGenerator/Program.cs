using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace MeshWeaver.TemplateGenerator;

public class Program
{
    private const string DefaultVersion = "2.2.0-local";
    private const string DefaultOutputPath = "dist/templates";

    public static void Main(string[] args)
    {
        var version = args.Length > 0 ? args[0] : DefaultVersion;
        var outputPath = args.Length > 1 ? args[1] : DefaultOutputPath;

        Console.WriteLine($"Creating MeshWeaver Project Templates v{version}");

        var generator = new TemplateGenerator(version, outputPath);
        generator.Generate();

        Console.WriteLine("Template creation completed successfully!");
        Console.WriteLine($"Output directory: {outputPath}");
        Console.WriteLine();
        Console.WriteLine("To create NuGet template package, run:");
        Console.WriteLine("  dotnet pack templates/packaging/MeshWeaver.ProjectTemplates.csproj -o nupkg");
        Console.WriteLine();
        Console.WriteLine("To test locally before packaging:");
        Console.WriteLine($"  cd {outputPath}");
        Console.WriteLine("  dotnet new install .");
        Console.WriteLine("  dotnet new meshweaver-solution -n TestApp");
        Console.WriteLine();
        Console.WriteLine("To install from NuGet (recommended):");
        Console.WriteLine("  dotnet new install MeshWeaver.ProjectTemplates");
    }
}

public class TemplateGenerator
{
    private readonly string _version;
    private readonly string _outputPath;

    public TemplateGenerator(string version, string outputPath)
    {
        _version = version;
        _outputPath = outputPath;
    }

    public void Generate()
    {
        CleanOutputDirectory();
        CopyTemplateProjects();
        CopyModules();
        UpdateNamespaces();
        RenameProjectFiles();
        UpdateProgramCs();
        UpdateProjectFiles();
        GenerateDirectoryPackagesProps();
        CopyClaude();
        CreateTemplateConfigs();
        CreateSolutionFile();
        CreateReadme();
    }

    private void CleanOutputDirectory()
    {
        Console.WriteLine("Cleaning output directory...");
        if (Directory.Exists(_outputPath))
        {
            Directory.Delete(_outputPath, true);
        }
        Directory.CreateDirectory(_outputPath);
    }

    private void CopyTemplateProjects()
    {
        Console.WriteLine("Copying template projects...");
        CopyDirectory("templates/MeshWeaverApp1.Portal", Path.Combine(_outputPath, "MeshWeaverApp1.Portal"));
    }

    private void CopyMarkdownDocumentation()
    {
        Console.WriteLine("Copying markdown documentation to template...");

        var markdownTargetDir = Path.Combine(_outputPath, "MeshWeaverApp1.Portal", "Markdown");
        Directory.CreateDirectory(markdownTargetDir);

        // Copy specific markdown files from root directory
        var rootMarkdownFiles = new[] { "Readme.md", "AREA_NESTING_BUG_FIX.md", "CLAUDE.md" };
        foreach (var file in rootMarkdownFiles)
        {
            var sourceFile = file;
            if (File.Exists(sourceFile))
            {
                var targetFile = Path.Combine(markdownTargetDir, file);
                File.Copy(sourceFile, targetFile, true);
                Console.WriteLine($"  Copied {file} to template");
            }
        }

        // Look for other documentation files that might be relevant
        var docDirectories = new[] { "modules/Documentation", "test/MeshWeaver.Documentation.Test/Markdown" };
        foreach (var docDir in docDirectories)
        {
            if (Directory.Exists(docDir))
            {
                foreach (var mdFile in Directory.GetFiles(docDir, "*.md"))
                {
                    var fileName = Path.GetFileName(mdFile);
                    var targetFile = Path.Combine(markdownTargetDir, fileName);
                    File.Copy(mdFile, targetFile, true);
                    Console.WriteLine($"  Copied {fileName} from {docDir}");
                }
            }
        }
    }

    private void CopyModules()
    {
        Console.WriteLine("Copying Todo module from modules...");
        CopyDirectory("modules/Todo/MeshWeaver.Todo", Path.Combine(_outputPath, "MeshWeaverApp1.Todo"), ["bin", "obj", ".gitignore"]);

        Console.WriteLine("Copying Todo.AI module from modules...");
        CopyDirectory("modules/Todo/MeshWeaver.Todo.AI", Path.Combine(_outputPath, "MeshWeaverApp1.Todo.AI"), ["bin", "obj", ".gitignore"]);

        Console.WriteLine("Copying Todo test project...");
        CopyDirectory("test/MeshWeaver.Todo.Test", Path.Combine(_outputPath, "MeshWeaverApp1.Todo.Test"), ["bin", "obj", "TestResults", ".gitignore"]);
    }

    private void UpdateNamespaces()
    {
        Console.WriteLine("Updating namespaces in all projects...");

        // Update Todo project namespaces
        UpdateNamespacesInDirectory(Path.Combine(_outputPath, "MeshWeaverApp1.Todo"),
            ["namespace MeshWeaver.Todo", "using MeshWeaver.Todo"],
            ["namespace MeshWeaverApp1.Todo", "using MeshWeaverApp1.Todo"]);

        // Update Todo.AI project namespaces
        UpdateNamespacesInDirectory(Path.Combine(_outputPath, "MeshWeaverApp1.Todo.AI"),
            ["namespace MeshWeaver.Todo.AI", "using MeshWeaver.Todo"],
            ["namespace MeshWeaverApp1.Todo.AI", "using MeshWeaverApp1.Todo"]);

        // Update Todo test project namespaces
        UpdateNamespacesInDirectory(Path.Combine(_outputPath, "MeshWeaverApp1.Todo.Test"),
            ["namespace MeshWeaver.Todo", "using MeshWeaver.Todo", "typeof(TodoApplicationAttribute)"],
            ["namespace MeshWeaverApp1.Todo", "using MeshWeaverApp1.Todo", "typeof(MeshWeaverApp1.Todo.TodoApplicationAttribute)"]);

        // Update Portal project namespaces
        UpdateNamespacesInDirectory(Path.Combine(_outputPath, "MeshWeaverApp1.Portal"),
            ["namespace MeshWeaverApp1.Portal", "using MeshWeaverApp1.Portal", "using MeshWeaver.Todo", "MeshWeaver.Todo.AI", "typeof(TodoApplicationAttribute)", "typeof(AgentsApplicationAttribute)"],
            ["namespace MeshWeaverApp1.Portal", "using MeshWeaverApp1.Portal", "using MeshWeaverApp1.Todo", "MeshWeaverApp1.Todo.AI", "typeof(MeshWeaverApp1.Todo.TodoApplicationAttribute)", "typeof(MeshWeaver.AI.Application.AgentsApplicationAttribute)"]);
    }

    private void RenameProjectFiles()
    {
        // Note: Portal project file should already be correctly named

        File.Move(
            Path.Combine(_outputPath, "MeshWeaverApp1.Todo", "MeshWeaver.Todo.csproj"),
            Path.Combine(_outputPath, "MeshWeaverApp1.Todo", "MeshWeaverApp1.Todo.csproj"));

        File.Move(
            Path.Combine(_outputPath, "MeshWeaverApp1.Todo.AI", "MeshWeaver.Todo.AI.csproj"),
            Path.Combine(_outputPath, "MeshWeaverApp1.Todo.AI", "MeshWeaverApp1.Todo.AI.csproj"));

        File.Move(
            Path.Combine(_outputPath, "MeshWeaverApp1.Todo.Test", "MeshWeaver.Todo.Test.csproj"),
            Path.Combine(_outputPath, "MeshWeaverApp1.Todo.Test", "MeshWeaverApp1.Todo.Test.csproj"));
    }

    private void UpdateProgramCs()
    {
        var programCsPath = Path.Combine(_outputPath, "MeshWeaverApp1.Portal", "Program.cs");
        var content = File.ReadAllText(programCsPath);

        // Update the template portal structure to include Todo references
        content = content.Replace("using MeshWeaver.Todo;", "using MeshWeaverApp1.Todo;\nusing MeshWeaverApp1.Todo.AI;");

        File.WriteAllText(programCsPath, content);
    }

    private void UpdateProjectFiles()
    {
        Console.WriteLine("Updating Portal project with package references...");
        var portalCsproj = $"""
            <Project Sdk="Microsoft.NET.Sdk.Web">

              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>

              <ItemGroup>
                <ProjectReference Include="..\MeshWeaverApp1.Todo\MeshWeaverApp1.Todo.csproj" />
                <ProjectReference Include="..\MeshWeaverApp1.Todo.AI\MeshWeaverApp1.Todo.AI.csproj" />
              </ItemGroup>

              <ItemGroup>
                <PackageReference Include="MeshWeaver.AI.Application" />
                <PackageReference Include="MeshWeaver.AI.AzureOpenAI" />
                <PackageReference Include="MeshWeaver.Blazor" />
                <PackageReference Include="MeshWeaver.Blazor.Chat" />
                <PackageReference Include="MeshWeaver.ContentCollections" />
                <PackageReference Include="MeshWeaver.Hosting.Blazor" />
                <PackageReference Include="MeshWeaver.Hosting.Monolith" />
                <PackageReference Include="MeshWeaver.Kernel.Hub" />
                <PackageReference Include="Microsoft.Extensions.Logging" />
              </ItemGroup>

              <ItemGroup>
                <Content Include="Markdown\**\*.md" CopyToOutputDirectory="PreserveNewest" />
                <Content Include="Markdown\**\*.png" CopyToOutputDirectory="PreserveNewest" />
                <Content Include="Markdown\**\*.jpg" CopyToOutputDirectory="PreserveNewest" />
                <Content Include="Markdown\**\*.jpeg" CopyToOutputDirectory="PreserveNewest" />
              </ItemGroup>

            </Project>
            """;
        File.WriteAllText(Path.Combine(_outputPath, "MeshWeaverApp1.Portal", "MeshWeaverApp1.Portal.csproj"), portalCsproj);

        Console.WriteLine("Updating Todo project with package references...");
        var todoCsproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="MeshWeaver.Mesh.Contract" />
                <PackageReference Include="MeshWeaver.Messaging.Hub" />
                <PackageReference Include="MeshWeaver.Data" />
                <PackageReference Include="MeshWeaver.Layout" />
              </ItemGroup>

            </Project>
            """;
        File.WriteAllText(Path.Combine(_outputPath, "MeshWeaverApp1.Todo", "MeshWeaverApp1.Todo.csproj"), todoCsproj);

        Console.WriteLine("Updating Todo.AI project with package references...");
        var todoAICsproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="MeshWeaver.AI" />
              </ItemGroup>

            </Project>
            """;
        File.WriteAllText(Path.Combine(_outputPath, "MeshWeaverApp1.Todo.AI", "MeshWeaverApp1.Todo.AI.csproj"), todoAICsproj);

        Console.WriteLine("Updating Test project with package references...");
        var testCsproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsPackable>false</IsPackable>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>

              <ItemGroup>
                <ProjectReference Include="..\MeshWeaverApp1.Todo\MeshWeaverApp1.Todo.csproj" />
              </ItemGroup>

              <ItemGroup>
                <PackageReference Include="MeshWeaver.Hosting.Monolith.TestBase" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
                <PackageReference Include="xunit.v3" />
                <PackageReference Include="xunit.v3.extensibility.core" />
                <PackageReference Include="xunit.runner.visualstudio" />
                <PackageReference Include="FluentAssertions" />
              </ItemGroup>

            </Project>
            """;
        File.WriteAllText(Path.Combine(_outputPath, "MeshWeaverApp1.Todo.Test", "MeshWeaverApp1.Todo.Test.csproj"), testCsproj);
    }

    private void GenerateDirectoryPackagesProps()
    {
        Console.WriteLine("Generating Directory.Packages.props with dynamic versions...");

        // Read versions from main solution's Directory.Packages.props
        var mainPackages = GetPackageVersions("Directory.Packages.props");
        
        // Read versions from test Directory.Packages.props
        var testPackages = GetPackageVersions("test/Directory.Packages.props");
        
        // Merge packages (test packages override main packages for conflicts)
        var allPackages = new Dictionary<string, string>(mainPackages);
        foreach (var package in testPackages)
        {
            allPackages[package.Key] = package.Value;
        }
        
        // Define MeshWeaver packages that should use the build version
        var meshWeaverPackages = new[]
        {
            "MeshWeaver.AI",
            "MeshWeaver.AI.Application", 
            "MeshWeaver.AI.AzureFoundry",
            "MeshWeaver.AI.AzureOpenAI",
            "MeshWeaver.Blazor",
            "MeshWeaver.Blazor.AgGrid",
            "MeshWeaver.Blazor.ChartJs", 
            "MeshWeaver.Blazor.Chat",
            "MeshWeaver.ContentCollections",
            "MeshWeaver.Data",
            "MeshWeaver.Hosting.Blazor",
            "MeshWeaver.Hosting.Monolith",
            "MeshWeaver.Hosting.Monolith.TestBase",
            "MeshWeaver.Kernel.Hub",
            "MeshWeaver.Layout",
            "MeshWeaver.Mesh.Contract",
            "MeshWeaver.Messaging.Hub"
        };
        
        // Override MeshWeaver package versions with the build version
        foreach (var package in meshWeaverPackages)
        {
            allPackages[package] = _version;
        }
        
        // Generate Directory.Packages.props content
        var sb = new StringBuilder();
        sb.AppendLine("<Project>");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine("  <ItemGroup>");
        sb.AppendLine($"    <!-- MeshWeaver packages - using build version {_version} -->");
        
        // Add MeshWeaver packages first
        foreach (var package in meshWeaverPackages.OrderBy(p => p))
        {
            if (allPackages.ContainsKey(package))
            {
                sb.AppendLine($"    <PackageVersion Include=\"{package}\" Version=\"{allPackages[package]}\" />");
            }
        }
        
        sb.AppendLine();
        sb.AppendLine("    <!-- Other packages -->");
        
        // Add all other packages
        foreach (var package in allPackages.OrderBy(p => p.Key))
        {
            if (!meshWeaverPackages.Contains(package.Key))
            {
                sb.AppendLine($"    <PackageVersion Include=\"{package.Key}\" Version=\"{package.Value}\" />");
            }
        }
        
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("</Project>");
        
        // Write Directory.Packages.props to the output directory
        File.WriteAllText(Path.Combine(_outputPath, "Directory.Packages.props"), sb.ToString());
        
        Console.WriteLine($"Directory.Packages.props generated with MeshWeaver version {_version}");
    }
    
    private Dictionary<string, string> GetPackageVersions(string filePath)
    {
        var packages = new Dictionary<string, string>();
        
        if (File.Exists(filePath))
        {
            var xml = XDocument.Load(filePath);
            var packageVersions = xml.Descendants("PackageVersion");
            
            foreach (var packageVersion in packageVersions)
            {
                var include = packageVersion.Attribute("Include")?.Value;
                var version = packageVersion.Attribute("Version")?.Value;
                
                if (!string.IsNullOrEmpty(include) && !string.IsNullOrEmpty(version))
                {
                    packages[include] = version;
                }
            }
        }
        
        return packages;
    }

    private void CopyClaude()
    {
        Console.WriteLine("Creating template-specific CLAUDE.md...");
        
        // Generate unique ports for each installation
        var httpPort = GenerateUniquePort(5000, 6000);
        var httpsPort = httpPort + 1;
        
        var claudeContent = CreateTemplateClaude(httpPort, httpsPort);
        var targetFile = Path.Combine(_outputPath, "CLAUDE.md");
        File.WriteAllText(targetFile, claudeContent);
        Console.WriteLine($"  Created template CLAUDE.md with ports {httpPort}/{httpsPort}");
        
        // Update launch settings with the generated ports
        UpdateLaunchSettingsPorts(httpPort, httpsPort);
    }
    
    private int GenerateUniquePort(int minPort, int maxPort)
    {
        var random = new Random();
        return random.Next(minPort, maxPort);
    }
    
    private void UpdateLaunchSettingsPorts(int httpPort, int httpsPort)
    {
        Console.WriteLine($"Updating launch settings with ports {httpPort}/{httpsPort}...");
        
        var launchSettingsPath = Path.Combine(_outputPath, "MeshWeaverApp1.Portal", "Properties", "launchSettings.json");
        if (!File.Exists(launchSettingsPath))
        {
            Console.WriteLine("  Warning: launchSettings.json not found");
            return;
        }
        
        var content = File.ReadAllText(launchSettingsPath);
        
        // Replace port numbers in the JSON
        content = content.Replace("\"applicationUrl\": \"http://localhost:5000\"", 
                                $"\"applicationUrl\": \"http://localhost:{httpPort}\"");
        content = content.Replace("\"applicationUrl\": \"https://localhost:5001;http://localhost:5000\"", 
                                $"\"applicationUrl\": \"https://localhost:{httpsPort};http://localhost:{httpPort}\"");
        content = content.Replace("\"sslPort\": 5001", $"\"sslPort\": {httpsPort}");
        
        File.WriteAllText(launchSettingsPath, content);
        Console.WriteLine("  Updated launchSettings.json with new ports");
    }
    
    private string CreateTemplateClaude(int httpPort, int httpsPort)
    {
        return $@"# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this MeshWeaverApp1 solution.

## Development Commands

### Build and Test
```bash
# Build entire solution
dotnet build

# Run tests (uses xUnit v3)
dotnet test

# Run specific test project
dotnet test MeshWeaverApp1.Todo.Test/MeshWeaverApp1.Todo.Test.csproj

# Clean solution
dotnet clean

# Restore packages
dotnet restore
```

### Running the Application

#### Portal Application
```bash
cd MeshWeaverApp1.Portal
dotnet run
# Access at http://localhost:{httpPort} or https://localhost:{httpsPort}
```

## Architecture Overview

### Core Concepts

**Message Hub Architecture**: MeshWeaver is built on an actor-model message hub system (`MeshWeaver.Messaging.Hub`). All application interactions flow through hierarchical message routing with address-based partitioning (e.g., `@app/Address/AreaName`).

**Layout Areas**: The UI system uses reactive Layout Areas - framework-agnostic UI abstractions that render in Blazor Server. Layout areas are addressed by route and automatically update via reactive streams.

**AI-First Design**: First-class AI integration using Semantic Kernel with plugins that provide agents access to application state and functionality.

### Project Structure

- **`MeshWeaverApp1.Portal/`** - Web application (Blazor Server)
- **`MeshWeaverApp1.Todo/`** - Todo business domain module  
- **`MeshWeaverApp1.Todo.AI/`** - AI agents for Todo functionality
- **`MeshWeaverApp1.Todo.Test/`** - Unit tests for Todo module

### Architectural Patterns

**Request-Response**: Use `hub.AwaitResponse<TResponse>(request, o => o.WithTarget(address))` for operations requiring results. 
The response is submitted as `hub.Post(responseMessage, o => o.ResponseFor(request))`.

**Fire-and-Forget**: Use `hub.Post(message, o => o.WithTarget(address))` for notifications and events.

**Address-Based Routing**: Services register at specific addresses (e.g., `app/todo`). 
Layout areas follow the pattern `@{{address}}/{{areaName}}/{{areaId}}`. The areaId is optional and depends on the view.

**Reactive UI**: All UI state changes flow through the message hub. Controls are immutable records that specify their current state.

## Development Patterns

### Adding New Layout Areas
```csharp
public static class MyLayoutArea
{{
    public static void AddMyLayoutArea(this LayoutConfiguration config) =>
        config.AddLayoutArea(nameof(MyLayout), MyLayout);

    public static UiControl MyLayout(LayoutAreaHost host, RenderingContext ctx) => 
        Controls.Stack
            .WithView(Controls.Html(""Some text""))
            .WithView(Controls.Markdown(""Some markdown view""));
}}
```

### Message Handling
```csharp
public static class MyHubConfiguration
{{
    public static MessageHubConfiguration AddMyHub(this MessageHubConfiguration config)
    {{
        return config.AddHandler<MyRequestAsync>(HandleMyRequestAsync)
                     .AddHandler<MyRequest>(HandleMyRequest);
    }}

    public static async Task<IMessageDelivery> HandleMyRequestAsync(MessageHub hub, IMessageDelivery<MyRequestAsync> request, CancellationToken ct)
    {{
        // Process the request
        var result = await SomeService.ProcessAsync(request.Message);
        
        // Send response
        await hub.Post(new MyResponse(result), o => o.ResponseFor(request));
        return request.Processed();
    }}
}}
```

### AI Plugin Development
```csharp
public class MyPlugin(IMessageHub hub, IAgentChat chat)
{{
    [KernelFunction]
    [Description(""Description on how to use"")]
    public async Task<string> DoSomething([Description(""Description for input"")]string input)
    {{
        var request = new MyRequest(input);
        var address = chat.Context.Address;
        var response = await hub.AwaitResponse<MyResponse>(request, o => o.WithTarget(address));
        return JsonSerializer.Serialize(response.Message, hub.JsonSerializationOptions);
    }}
}}
```

## Key Dependencies

- **.NET 9.0** - Target framework
- **Blazor Server** - Web UI framework  
- **Semantic Kernel** - AI integration
- **xUnit v3** - Testing framework
- **FluentAssertions** - Test assertions
- **MeshWeaver** - Framework packages

## Testing Guidelines

Tests use xUnit v3 with MeshWeaver.Hosting.Monolith.TestBase:

```csharp
public class MyTest : HubTestBase, IAsyncLifetime
{{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration config)
    {{
        return base.ConfigureHost(config)
            .AddTodoHub(); // Register your hub
    }}

    [Fact]
    public async Task MyTestMethod()
    {{
        // Arrange
        var request = new MyRequest(""test input"");
        var hub = GetClient();

        // Act
        var response = await hub.AwaitResponse<MyResponse>(request, o => o.WithTarget(new HostAddress()));
        
        // Assert
        response.Should().NotBeNull();
        response.Message.Result.Should().Be(""expected result"");
    }}
}}
```

## Configuration

The solution uses centralized package management via `Directory.Packages.props`. All package versions are managed centrally.

Current MeshWeaver version: {_version}
";
    }

    private void CreateTemplateConfigs()
    {
        Console.WriteLine("Creating template metadata files...");

        CreateTemplateConfig("MeshWeaverApp1.Portal", new TemplateConfig
        {
            Schema = "http://json.schemastore.org/template",
            Author = "Systemorph",
            Classifications = ["Web", "Portal", "MeshWeaver"],
            Name = "MeshWeaver Portal Application",
            Identity = "MeshWeaver.Portal.CSharp",
            GroupIdentity = "MeshWeaver.Portal",
            ShortName = "meshweaver-portal",
            Tags = new { language = "C#", type = "project" },
            SourceName = "MeshWeaverApp1.Portal",
            PreferNameDirectory = true,
            Symbols = new
            {
                Framework = new
                {
                    type = "parameter",
                    description = "The target framework for the project.",
                    datatype = "choice",
                    choices = new[] { new { choice = "net9.0", description = ".NET 9.0" } },
                    defaultValue = "net9.0",
                    replaces = "net9.0"
                }
            },
            PrimaryOutputs = new[] { new { path = "MeshWeaverApp1.Portal.csproj" } },
            PostActions = new[]
            {
                new
                {
                    description = "Restore NuGet packages required by this project.",
                    manualInstructions = new[] { new { text = "Run 'dotnet restore'" } },
                    actionId = "210D431B-A78B-4D2F-B762-4ED3E3EA9025",
                    continueOnError = true
                }
            }
        });

        CreateTemplateConfig("MeshWeaverApp1.Todo", new TemplateConfig
        {
            Schema = "http://json.schemastore.org/template",
            Author = "Systemorph",
            Classifications = ["Library", "MeshWeaver", "Todo"],
            Name = "MeshWeaver Todo Library",
            Identity = "MeshWeaver.Todo.CSharp",
            GroupIdentity = "MeshWeaver.Todo",
            ShortName = "meshweaver-todo",
            Tags = new { language = "C#", type = "project" },
            SourceName = "MeshWeaverApp1.Todo",
            PreferNameDirectory = true,
            Symbols = new
            {
                Framework = new
                {
                    type = "parameter",
                    description = "The target framework for the project.",
                    datatype = "choice",
                    choices = new[] { new { choice = "net9.0", description = ".NET 9.0" } },
                    defaultValue = "net9.0",
                    replaces = "net9.0"
                }
            },
            PrimaryOutputs = new[] { new { path = "MeshWeaverApp1.Todo.csproj" } }
        });

        CreateTemplateConfig("MeshWeaverApp1.Todo.AI", new TemplateConfig
        {
            Schema = "http://json.schemastore.org/template",
            Author = "Systemorph",
            Classifications = ["Library", "MeshWeaver", "Todo", "AI"],
            Name = "MeshWeaver Todo AI Library",
            Identity = "MeshWeaver.Todo.AI.CSharp",
            GroupIdentity = "MeshWeaver.Todo.AI",
            ShortName = "meshweaver-todo-ai",
            Tags = new { language = "C#", type = "project" },
            SourceName = "MeshWeaverApp1.Todo.AI",
            PreferNameDirectory = true,
            Symbols = new
            {
                Framework = new
                {
                    type = "parameter",
                    description = "The target framework for the project.",
                    datatype = "choice",
                    choices = new[] { new { choice = "net9.0", description = ".NET 9.0" } },
                    defaultValue = "net9.0",
                    replaces = "net9.0"
                }
            },
            PrimaryOutputs = new[] { new { path = "MeshWeaverApp1.Todo.AI.csproj" } }
        });

        CreateTemplateConfig("MeshWeaverApp1.Todo.Test", new TemplateConfig
        {
            Schema = "http://json.schemastore.org/template",
            Author = "Systemorph",
            Classifications = ["Test", "MeshWeaver", "xUnit"],
            Name = "MeshWeaver Todo Test Project",
            Identity = "MeshWeaver.Todo.Test.CSharp",
            GroupIdentity = "MeshWeaver.Todo.Test",
            ShortName = "meshweaver-test",
            Tags = new { language = "C#", type = "project" },
            SourceName = "MeshWeaverApp1.Todo.Test",
            PreferNameDirectory = true,
            Symbols = new
            {
                Framework = new
                {
                    type = "parameter",
                    description = "The target framework for the project.",
                    datatype = "choice",
                    choices = new[] { new { choice = "net9.0", description = ".NET 9.0" } },
                    defaultValue = "net9.0",
                    replaces = "net9.0"
                }
            },
            PrimaryOutputs = new[] { new { path = "MeshWeaverApp1.Todo.Test.csproj" } }
        });

        // Solution template
        CreateTemplateConfig("", new TemplateConfig
        {
            Schema = "http://json.schemastore.org/template",
            Author = "Systemorph",
            Classifications = ["Solution", "MeshWeaver", "Web", "Portal"],
            Name = "MeshWeaver Portal with Todo Application",
            Identity = "MeshWeaver.PortalSolution.CSharp",
            GroupIdentity = "MeshWeaver.PortalSolution",
            ShortName = "meshweaver-solution",
            Tags = new { language = "C#", type = "solution" },
            SourceName = "MeshWeaverApp1",
            PreferNameDirectory = true,
            Symbols = new
            {
                Framework = new
                {
                    type = "parameter",
                    description = "The target framework for the project.",
                    datatype = "choice",
                    choices = new[] { new { choice = "net9.0", description = ".NET 9.0" } },
                    defaultValue = "net9.0",
                    replaces = "net9.0"
                }
            },
            PrimaryOutputs = new[]
            {
                new { path = "MeshWeaverApp1.Portal/MeshWeaverApp1.Portal.csproj" },
                new { path = "MeshWeaverApp1.Todo/MeshWeaverApp1.Todo.csproj" },
                new { path = "MeshWeaverApp1.Todo.AI/MeshWeaverApp1.Todo.AI.csproj" },
                new { path = "MeshWeaverApp1.Todo.Test/MeshWeaverApp1.Todo.Test.csproj" }
            },
            PostActions = new[]
            {
                new
                {
                    description = "Restore NuGet packages required by this project.",
                    manualInstructions = new[] { new { text = "Run 'dotnet restore'" } },
                    actionId = "210D431B-A78B-4D2F-B762-4ED3E3EA9025",
                    continueOnError = true
                }
            }
        });
    }

    private void CreateSolutionFile()
    {
        var solutionContent = """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            VisualStudioVersion = 17.0.31903.59
            MinimumVisualStudioVersion = 10.0.40219.1
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "MeshWeaverApp1.Portal", "MeshWeaverApp1.Portal\MeshWeaverApp1.Portal.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "MeshWeaverApp1.Todo", "MeshWeaverApp1.Todo\MeshWeaverApp1.Todo.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "MeshWeaverApp1.Todo.AI", "MeshWeaverApp1.Todo.AI\MeshWeaverApp1.Todo.AI.csproj", "{44444444-4444-4444-4444-444444444444}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "MeshWeaverApp1.Todo.Test", "MeshWeaverApp1.Todo.Test\MeshWeaverApp1.Todo.Test.csproj", "{33333333-3333-3333-3333-333333333333}"
            EndProject
            Global
            	GlobalSection(SolutionConfigurationPlatforms) = preSolution
            		Debug|Any CPU = Debug|Any CPU
            		Release|Any CPU = Release|Any CPU
            	EndGlobalSection
            	GlobalSection(ProjectConfigurationPlatforms) = postSolution
            		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
            		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.ActiveCfg = Release|Any CPU
            		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.Build.0 = Release|Any CPU
            		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
            		{22222222-2222-2222-2222-222222222222}.Release|Any CPU.ActiveCfg = Release|Any CPU
            		{22222222-2222-2222-2222-222222222222}.Release|Any CPU.Build.0 = Release|Any CPU
            		{44444444-4444-4444-4444-444444444444}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            		{44444444-4444-4444-4444-444444444444}.Debug|Any CPU.Build.0 = Debug|Any CPU
            		{44444444-4444-4444-4444-444444444444}.Release|Any CPU.ActiveCfg = Release|Any CPU
            		{44444444-4444-4444-4444-444444444444}.Release|Any CPU.Build.0 = Release|Any CPU
            		{33333333-3333-3333-3333-333333333333}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            		{33333333-3333-3333-3333-333333333333}.Debug|Any CPU.Build.0 = Debug|Any CPU
            		{33333333-3333-3333-3333-333333333333}.Release|Any CPU.ActiveCfg = Release|Any CPU
            		{33333333-3333-3333-3333-333333333333}.Release|Any CPU.Build.0 = Release|Any CPU
            	EndGlobalSection
            EndGlobal
            """;
        File.WriteAllText(Path.Combine(_outputPath, "MeshWeaverApp1.sln"), solutionContent);
    }

    private void CreateReadme()
    {
        var readmeContent = $"""
            # MeshWeaver Portal Solution Template

            This template creates a complete MeshWeaver portal application with a Todo module example.

            ## Projects Included

            - **MeshWeaverApp1.Portal**: The main web portal application
            - **MeshWeaverApp1.Todo**: A Todo module demonstrating MeshWeaver data management and layout areas
            - **MeshWeaverApp1.Todo.AI**: AI agent for Todo management with natural language processing
            - **MeshWeaverApp1.Todo.Test**: Unit tests for the Todo module

            ## Getting Started

            1. Install the template package: `dotnet new install MeshWeaver.ProjectTemplates`
            2. Create a new project using: `dotnet new meshweaver-solution -n MyApp`
            3. Navigate to the created directory: `cd MyApp`
            4. Run the application: `dotnet run --project MyApp.Portal`

            ## Features

            - Complete portal infrastructure
            - Reactive data management
            - Layout areas with real-time updates
            - AI-powered Todo assistant with natural language processing
            - Comprehensive testing setup
            - Sample Todo application with CRUD operations

            ## Individual Templates

            You can also create individual projects:

            - Portal only: `dotnet new meshweaver-portal -n MyPortal`
            - Todo library only: `dotnet new meshweaver-todo -n MyTodo`
            - Todo AI library only: `dotnet new meshweaver-todo-ai -n MyTodoAI`
            - Test project only: `dotnet new meshweaver-test -n MyTests`

            ## MeshWeaver Version

            This template is based on MeshWeaver version {_version}.

            ## Development

            To run the application locally:

            1. Navigate to the Portal project: `cd MeshWeaverApp1.Portal`
            2. Run: `dotnet run`
            3. Open browser to: `https://localhost:5001`

            The Todo application will be available in the portal with sample data.
            """;
        File.WriteAllText(Path.Combine(_outputPath, "README.md"), readmeContent);
    }

    private void CreateTemplateConfig(string projectPath, TemplateConfig config)
    {
        var configDir = string.IsNullOrEmpty(projectPath)
            ? Path.Combine(_outputPath, ".template.config")
            : Path.Combine(_outputPath, projectPath, ".template.config");

        Directory.CreateDirectory(configDir);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(config, options);
        File.WriteAllText(Path.Combine(configDir, "template.json"), json);
    }

    private void CopyDirectory(string sourceDir, string targetDir, string[]? excludeItems = null)
    {
        Directory.CreateDirectory(targetDir);

        excludeItems ??= [];

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            // Skip files that match exclusion patterns
            if (!excludeItems.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            {
                var destFile = Path.Combine(targetDir, fileName);
                File.Copy(file, destFile, true);
            }
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(subDir);
            if (!excludeItems.Contains(dirName, StringComparer.OrdinalIgnoreCase))
            {
                CopyDirectory(subDir, Path.Combine(targetDir, dirName), excludeItems);
            }
        }
    }

    private void UpdateNamespacesInDirectory(string directory, string[] oldPatterns, string[] newPatterns)
    {
        var csFiles = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories);

        foreach (var file in csFiles)
        {
            var content = File.ReadAllText(file);

            for (int i = 0; i < oldPatterns.Length; i++)
            {
                content = content.Replace(oldPatterns[i], newPatterns[i]);
            }

            File.WriteAllText(file, content);
        }
    }
}

public class TemplateConfig
{
    public string Schema { get; set; } = "";
    public string Author { get; set; } = "";
    public string[] Classifications { get; set; } = [];
    public string Name { get; set; } = "";
    public string Identity { get; set; } = "";
    public string GroupIdentity { get; set; } = "";
    public string ShortName { get; set; } = "";
    public object Tags { get; set; } = new();
    public string SourceName { get; set; } = "";
    public bool PreferNameDirectory { get; set; }
    public object Symbols { get; set; } = new();
    public object[] PrimaryOutputs { get; set; } = [];
    public object[]? PostActions { get; set; }
}
