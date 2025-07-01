using System.Text;
using System.Text.Json;

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

    private void CopyModules()
    {
        Console.WriteLine("Copying Todo module from modules...");
        CopyDirectory("modules/Todo/MeshWeaver.Todo", Path.Combine(_outputPath, "MeshWeaverApp1.Todo"), ["bin", "obj"]);

        Console.WriteLine("Copying Todo.AI module from modules...");
        CopyDirectory("modules/Todo/MeshWeaver.Todo.AI", Path.Combine(_outputPath, "MeshWeaverApp1.Todo.AI"), ["bin", "obj"]);

        Console.WriteLine("Copying Todo test project...");
        CopyDirectory("test/MeshWeaver.Todo.Test", Path.Combine(_outputPath, "MeshWeaverApp1.TodoTest"), ["bin", "obj", "TestResults"]);
    }

    private void UpdateNamespaces()
    {
        Console.WriteLine("Updating namespaces in Todo projects...");

        // Update Todo project namespaces
        UpdateNamespacesInDirectory(Path.Combine(_outputPath, "MeshWeaverApp1.Todo"),
            ["namespace MeshWeaver.Todo", "using MeshWeaver.Todo"],
            ["namespace MeshWeaverApp1.Todo", "using MeshWeaverApp1.Todo"]);

        // Update Todo.AI project namespaces
        UpdateNamespacesInDirectory(Path.Combine(_outputPath, "MeshWeaverApp1.Todo.AI"),
            ["namespace MeshWeaver.Todo.AI", "using MeshWeaver.Todo"],
            ["namespace MeshWeaverApp1.Todo.AI", "using MeshWeaverApp1.Todo"]);

        // Update Todo test project namespaces
        UpdateNamespacesInDirectory(Path.Combine(_outputPath, "MeshWeaverApp1.TodoTest"),
            ["namespace MeshWeaver.Todo", "using MeshWeaver.Todo", "typeof(TodoApplicationAttribute)"],
            ["namespace MeshWeaverApp1.Todo", "using MeshWeaverApp1.Todo", "typeof(MeshWeaverApp1.Todo.TodoApplicationAttribute)"]);
    }

    private void RenameProjectFiles()
    {
        File.Move(
            Path.Combine(_outputPath, "MeshWeaverApp1.Todo", "MeshWeaver.Todo.csproj"),
            Path.Combine(_outputPath, "MeshWeaverApp1.Todo", "MeshWeaverApp1.Todo.csproj"));

        File.Move(
            Path.Combine(_outputPath, "MeshWeaverApp1.Todo.AI", "MeshWeaver.Todo.AI.csproj"),
            Path.Combine(_outputPath, "MeshWeaverApp1.Todo.AI", "MeshWeaverApp1.Todo.AI.csproj"));

        File.Move(
            Path.Combine(_outputPath, "MeshWeaverApp1.TodoTest", "MeshWeaver.Todo.Test.csproj"),
            Path.Combine(_outputPath, "MeshWeaverApp1.TodoTest", "MeshWeaverApp1.TodoTest.csproj"));
    }

    private void UpdateProgramCs()
    {
        var programCsPath = Path.Combine(_outputPath, "MeshWeaverApp1.Portal", "Program.cs");
        var content = File.ReadAllText(programCsPath);

        content = content.Replace("using MeshWeaver.Todo;", "using MeshWeaverApp1.Todo;");
        content = content.Replace("using MeshWeaver.Todo.AI;", "using MeshWeaverApp1.Todo.AI;");
        content = content.Replace("typeof(TodoApplicationAttribute)", "typeof(MeshWeaverApp1.Todo.TodoApplicationAttribute)");
        content = content.Replace("typeof(TodoAgent)", "typeof(MeshWeaverApp1.Todo.AI.TodoAgent)");

        File.WriteAllText(programCsPath, content);

        // Also update MeshConfiguration.cs in Portal project
        var meshConfigPath = Path.Combine(_outputPath, "MeshWeaverApp1.Portal", "MeshConfiguration.cs");
        if (File.Exists(meshConfigPath))
        {
            var meshConfigContent = File.ReadAllText(meshConfigPath);
            meshConfigContent = meshConfigContent.Replace("using MeshWeaver.Todo;", "using MeshWeaverApp1.Todo;");
            meshConfigContent = meshConfigContent.Replace("using MeshWeaver.Todo.AI;", "using MeshWeaverApp1.Todo.AI;");
            meshConfigContent = meshConfigContent.Replace("typeof(TodoApplicationAttribute)", "typeof(MeshWeaverApp1.Todo.TodoApplicationAttribute)");
            File.WriteAllText(meshConfigPath, meshConfigContent);
        }

        // Also update SharedPortalConfiguration.cs in Portal project
        var sharedConfigPath = Path.Combine(_outputPath, "MeshWeaverApp1.Portal", "SharedPortalConfiguration.cs");
        if (File.Exists(sharedConfigPath))
        {
            var sharedConfigContent = File.ReadAllText(sharedConfigPath);
            sharedConfigContent = sharedConfigContent.Replace("using MeshWeaver.Todo;", "using MeshWeaverApp1.Todo;");
            sharedConfigContent = sharedConfigContent.Replace("using MeshWeaver.Todo.AI;", "using MeshWeaverApp1.Todo.AI;");
            sharedConfigContent = sharedConfigContent.Replace("typeof(TodoApplicationAttribute)", "typeof(MeshWeaverApp1.Todo.TodoApplicationAttribute)");
            sharedConfigContent = sharedConfigContent.Replace("typeof(TodoAgent)", "typeof(MeshWeaverApp1.Todo.AI.TodoAgent)");
            File.WriteAllText(sharedConfigPath, sharedConfigContent);
        }
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
                <PackageReference Include="MeshWeaver.AI.AzureOpenAI" Version="{_version}" />
                <PackageReference Include="MeshWeaver.Blazor.Chat" Version="{_version}" />
                <PackageReference Include="MeshWeaver.Blazor" Version="{_version}" />
                <PackageReference Include="MeshWeaver.Hosting.Blazor" Version="{_version}" />
                <PackageReference Include="MeshWeaver.Hosting.Monolith" Version="{_version}" />
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
                <PackageReference Include="MeshWeaver.Mesh.Contract" Version="{_version}" />
                <PackageReference Include="MeshWeaver.Messaging.Hub" Version="{_version}" />
                <PackageReference Include="MeshWeaver.Data" Version="{_version}" />
                <PackageReference Include="MeshWeaver.Layout" Version="{_version}" />
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
                <PackageReference Include="MeshWeaver.AI" Version="{_version}" />
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
                <PackageReference Include="MeshWeaver.Hosting.Monolith.TestBase" Version="{_version}" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
                <PackageReference Include="FluentAssertions" Version="6.12.2" />
              </ItemGroup>

            </Project>
            """;
        File.WriteAllText(Path.Combine(_outputPath, "MeshWeaverApp1.TodoTest", "MeshWeaverApp1.TodoTest.csproj"), testCsproj);
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

        CreateTemplateConfig("MeshWeaverApp1.TodoTest", new TemplateConfig
        {
            Schema = "http://json.schemastore.org/template",
            Author = "Systemorph",
            Classifications = ["Test", "MeshWeaver", "xUnit"],
            Name = "MeshWeaver Todo Test Project",
            Identity = "MeshWeaver.Todo.Test.CSharp",
            GroupIdentity = "MeshWeaver.Todo.Test",
            ShortName = "meshweaver-test",
            Tags = new { language = "C#", type = "project" },
            SourceName = "MeshWeaverApp1.TodoTest",
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
            PrimaryOutputs = new[] { new { path = "MeshWeaverApp1.TodoTest.csproj" } }
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
                new { path = "MeshWeaverApp1.TodoTest/MeshWeaverApp1.TodoTest.csproj" }
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
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "MeshWeaverApp1.TodoTest", "MeshWeaverApp1.TodoTest\MeshWeaverApp1.TodoTest.csproj", "{33333333-3333-3333-3333-333333333333}"
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
            - **MeshWeaverApp1.TodoTest**: Unit tests for the Todo module

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

    private void CopyDirectory(string sourceDir, string targetDir, string[]? excludeDirectories = null)
    {
        Directory.CreateDirectory(targetDir);

        excludeDirectories ??= [];

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            var destFile = Path.Combine(targetDir, fileName);
            File.Copy(file, destFile, true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(subDir);
            if (!excludeDirectories.Contains(dirName, StringComparer.OrdinalIgnoreCase))
            {
                CopyDirectory(subDir, Path.Combine(targetDir, dirName), excludeDirectories);
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
