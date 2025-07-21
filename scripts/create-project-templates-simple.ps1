#!/usr/bin/env pwsh

param(
    [string]$Version = "2.2.0-rc5",
    [string]$OutputPath = "dist\templates"
)

$ErrorActionPreference = "Stop"

Write-Host "Creating MeshWeaver Project Templates v$Version" -ForegroundColor Green

# Clean output directory
if (Test-Path $OutputPath) {
    Remove-Item -Path $OutputPath -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null

# Copy template projects
Write-Host "Copying template projects..." -ForegroundColor Yellow
Copy-Item -Path "templates\MeshWeaverApp1.Portal" -Destination "$OutputPath\MeshWeaverApp1.Portal" -Recurse -Force

# Copy and rename Todo module from modules directory
Write-Host "Copying Todo module from modules..." -ForegroundColor Yellow
Copy-Item -Path "modules\Todo\MeshWeaver.Todo" -Destination "$OutputPath\MeshWeaverApp1.Todo" -Recurse -Force -Exclude "bin", "obj"

# Copy and rename Todo.AI module from modules directory
Write-Host "Copying Todo.AI module from modules..." -ForegroundColor Yellow
Copy-Item -Path "modules\Todo\MeshWeaver.Todo.AI" -Destination "$OutputPath\MeshWeaverApp1.Todo.AI" -Recurse -Force -Exclude "bin", "obj"

# Copy and rename Todo test project
Write-Host "Copying Todo test project..." -ForegroundColor Yellow
Copy-Item -Path "test\MeshWeaver.Todo.Test" -Destination "$OutputPath\MeshWeaverApp1.TodoTest" -Recurse -Force -Exclude "bin", "obj", "TestResults"

# Update namespaces in copied Todo projects
Write-Host "Updating namespaces in Todo projects..." -ForegroundColor Yellow

# Update Todo project namespaces
$todoFiles = Get-ChildItem -Path "$OutputPath\MeshWeaverApp1.Todo" -Recurse -Include "*.cs"
foreach ($file in $todoFiles) {
    $content = Get-Content $file.FullName
    $content = $content -replace 'namespace MeshWeaver\.Todo', 'namespace MeshWeaverApp1.Todo'
    $content = $content -replace 'using MeshWeaver\.Todo', 'using MeshWeaverApp1.Todo'
    Set-Content -Path $file.FullName -Value $content
}

# Update Todo.AI project namespaces
$todoAIFiles = Get-ChildItem -Path "$OutputPath\MeshWeaverApp1.Todo.AI" -Recurse -Include "*.cs"
foreach ($file in $todoAIFiles) {
    $content = Get-Content $file.FullName
    $content = $content -replace 'namespace MeshWeaver\.Todo\.AI', 'namespace MeshWeaverApp1.Todo.AI'
    $content = $content -replace 'using MeshWeaver\.Todo', 'using MeshWeaverApp1.Todo'
    Set-Content -Path $file.FullName -Value $content
}

# Update Todo test project namespaces
$testFiles = Get-ChildItem -Path "$OutputPath\MeshWeaverApp1.TodoTest" -Recurse -Include "*.cs"
foreach ($file in $testFiles) {
    $content = Get-Content $file.FullName
    $content = $content -replace 'namespace MeshWeaver\.Todo', 'namespace MeshWeaverApp1.Todo'
    $content = $content -replace 'using MeshWeaver\.Todo', 'using MeshWeaverApp1.Todo'
    $content = $content -replace 'typeof\(TodoApplicationAttribute\)', 'typeof(MeshWeaverApp1.Todo.TodoApplicationAttribute)'
    Set-Content -Path $file.FullName -Value $content
}

# Rename project files
Rename-Item -Path "$OutputPath\MeshWeaverApp1.Todo\MeshWeaver.Todo.csproj" -NewName "MeshWeaverApp1.Todo.csproj"
Rename-Item -Path "$OutputPath\MeshWeaverApp1.Todo.AI\MeshWeaver.Todo.AI.csproj" -NewName "MeshWeaverApp1.Todo.AI.csproj"
Rename-Item -Path "$OutputPath\MeshWeaverApp1.TodoTest\MeshWeaver.Todo.Test.csproj" -NewName "MeshWeaverApp1.TodoTest.csproj"

# Update Portal Program.cs to use new namespace
$portalProgramCs = Get-Content "$OutputPath\MeshWeaverApp1.Portal\Program.cs"
$portalProgramCs = $portalProgramCs -replace 'using MeshWeaver\.Todo;', 'using MeshWeaverApp1.Todo;'
$portalProgramCs = $portalProgramCs -replace 'using MeshWeaver\.Todo\.AI;', 'using MeshWeaverApp1.Todo.AI;'
$portalProgramCs = $portalProgramCs -replace 'typeof\(TodoApplicationAttribute\)', 'typeof(MeshWeaverApp1.Todo.TodoApplicationAttribute)'
$portalProgramCs = $portalProgramCs -replace 'typeof\(TodoAgent\)', 'typeof(MeshWeaverApp1.Todo.AI.TodoAgent)'
Set-Content -Path "$OutputPath\MeshWeaverApp1.Portal\Program.cs" -Value $portalProgramCs

# Update Portal project file with package references
Write-Host "Updating Portal project with package references..." -ForegroundColor Cyan
$portalCsproj = @"
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
    <PackageReference Include="MeshWeaver.AI.AzureOpenAI" Version="$Version" />
    <PackageReference Include="MeshWeaver.Blazor.Chat" Version="$Version" />
    <PackageReference Include="MeshWeaver.Blazor" Version="$Version" />
    <PackageReference Include="MeshWeaver.Hosting.Blazor" Version="$Version" />
    <PackageReference Include="MeshWeaver.Hosting.Monolith" Version="$Version" />
  </ItemGroup>

</Project>
"@

$portalCsproj | Out-File -FilePath "$OutputPath\MeshWeaverApp1.Portal\MeshWeaverApp1.Portal.csproj" -Encoding utf8

# Update Todo project file with package references
Write-Host "Updating Todo project with package references..." -ForegroundColor Cyan
$todoCsproj = @"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="MeshWeaver.Mesh.Contract" Version="$Version" />
    <PackageReference Include="MeshWeaver.Messaging.Hub" Version="$Version" />
    <PackageReference Include="MeshWeaver.Data" Version="$Version" />
    <PackageReference Include="MeshWeaver.Layout" Version="$Version" />
  </ItemGroup>

</Project>
"@

$todoCsproj | Out-File -FilePath "$OutputPath\MeshWeaverApp1.Todo\MeshWeaverApp1.Todo.csproj" -Encoding utf8

# Update Todo.AI project file with package references
Write-Host "Updating Todo.AI project with package references..." -ForegroundColor Cyan
$todoAICsproj = @"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="MeshWeaver.AI" Version="$Version" />
  </ItemGroup>

</Project>
"@

$todoAICsproj | Out-File -FilePath "$OutputPath\MeshWeaverApp1.Todo.AI\MeshWeaverApp1.Todo.AI.csproj" -Encoding utf8

# Update Test project file with package references
Write-Host "Updating Test project with package references..." -ForegroundColor Cyan
$testCsproj = @"
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
    <PackageReference Include="MeshWeaver.Hosting.Monolith.TestBase" Version="$Version" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit.v3" Version="3.0.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.1" />
  </ItemGroup>

</Project>
"@

$testCsproj | Out-File -FilePath "$OutputPath\MeshWeaverApp1.TodoTest\MeshWeaverApp1.TodoTest.csproj" -Encoding utf8

# Create template.json files
Write-Host "Creating template metadata files..." -ForegroundColor Yellow

# Portal template
New-Item -ItemType Directory -Path "$OutputPath\MeshWeaverApp1.Portal\.template.config" -Force | Out-Null
$portalTemplate = @{
    '$schema'           = "http://json.schemastore.org/template"
    author              = "Systemorph"
    classifications     = @("Web", "Portal", "MeshWeaver")
    name                = "MeshWeaver Portal Application"
    identity            = "MeshWeaver.Portal.CSharp"
    groupIdentity       = "MeshWeaver.Portal"
    shortName           = "meshweaver-portal"
    tags                = @{
        language = "C#"
        type     = "project"
    }
    sourceName          = "MeshWeaverApp1.Portal"
    preferNameDirectory = $true
    symbols             = @{
        Framework = @{
            type         = "parameter"
            description  = "The target framework for the project."
            datatype     = "choice"
            choices      = @(
                @{
                    choice      = "net9.0"
                    description = ".NET 9.0"
                }
            )
            defaultValue = "net9.0"
            replaces     = "net9.0"
        }
    }
    primaryOutputs      = @(
        @{
            path = "MeshWeaverApp1.Portal.csproj"
        }
    )
    postActions         = @(
        @{
            description        = "Restore NuGet packages required by this project."
            manualInstructions = @(
                @{
                    text = "Run 'dotnet restore'"
                }
            )
            actionId           = "210D431B-A78B-4D2F-B762-4ED3E3EA9025"
            continueOnError    = $true
        }
    )
} | ConvertTo-Json -Depth 10

$portalTemplate | Out-File -FilePath "$OutputPath\MeshWeaverApp1.Portal\.template.config\template.json" -Encoding utf8

# Todo template
New-Item -ItemType Directory -Path "$OutputPath\MeshWeaverApp1.Todo\.template.config" -Force | Out-Null
$todoTemplate = @{
    '$schema'           = "http://json.schemastore.org/template"
    author              = "Systemorph"
    classifications     = @("Library", "MeshWeaver", "Todo")
    name                = "MeshWeaver Todo Library"
    identity            = "MeshWeaver.Todo.CSharp"
    groupIdentity       = "MeshWeaver.Todo"
    shortName           = "meshweaver-todo"
    tags                = @{
        language = "C#"
        type     = "project"
    }
    sourceName          = "MeshWeaverApp1.Todo"
    preferNameDirectory = $true
    symbols             = @{
        Framework = @{
            type         = "parameter"
            description  = "The target framework for the project."
            datatype     = "choice"
            choices      = @(
                @{
                    choice      = "net9.0"
                    description = ".NET 9.0"
                }
            )
            defaultValue = "net9.0"
            replaces     = "net9.0"
        }
    }
    primaryOutputs      = @(
        @{
            path = "MeshWeaverApp1.Todo.csproj"
        }
    )
} | ConvertTo-Json -Depth 10

$todoTemplate | Out-File -FilePath "$OutputPath\MeshWeaverApp1.Todo\.template.config\template.json" -Encoding utf8

# Todo.AI template
New-Item -ItemType Directory -Path "$OutputPath\MeshWeaverApp1.Todo.AI\.template.config" -Force | Out-Null
$todoAITemplate = @{
    '$schema'           = "http://json.schemastore.org/template"
    author              = "Systemorph"
    classifications     = @("Library", "MeshWeaver", "Todo", "AI")
    name                = "MeshWeaver Todo AI Library"
    identity            = "MeshWeaver.Todo.AI.CSharp"
    groupIdentity       = "MeshWeaver.Todo.AI"
    shortName           = "meshweaver-todo-ai"
    tags                = @{
        language = "C#"
        type     = "project"
    }
    sourceName          = "MeshWeaverApp1.Todo.AI"
    preferNameDirectory = $true
    symbols             = @{
        Framework = @{
            type         = "parameter"
            description  = "The target framework for the project."
            datatype     = "choice"
            choices      = @(
                @{
                    choice      = "net9.0"
                    description = ".NET 9.0"
                }
            )
            defaultValue = "net9.0"
            replaces     = "net9.0"
        }
    }
    primaryOutputs      = @(
        @{
            path = "MeshWeaverApp1.Todo.AI.csproj"
        }
    )
} | ConvertTo-Json -Depth 10

$todoAITemplate | Out-File -FilePath "$OutputPath\MeshWeaverApp1.Todo.AI\.template.config\template.json" -Encoding utf8

# Test template
New-Item -ItemType Directory -Path "$OutputPath\MeshWeaverApp1.TodoTest\.template.config" -Force | Out-Null
$testTemplate = @{
    '$schema'           = "http://json.schemastore.org/template"
    author              = "Systemorph"
    classifications     = @("Test", "MeshWeaver", "xUnit")
    name                = "MeshWeaver Todo Test Project"
    identity            = "MeshWeaver.Todo.Test.CSharp"
    groupIdentity       = "MeshWeaver.Todo.Test"
    shortName           = "meshweaver-test"
    tags                = @{
        language = "C#"
        type     = "project"
    }
    sourceName          = "MeshWeaverApp1.TodoTest"
    preferNameDirectory = $true
    symbols             = @{
        Framework = @{
            type         = "parameter"
            description  = "The target framework for the project."
            datatype     = "choice"
            choices      = @(
                @{
                    choice      = "net9.0"
                    description = ".NET 9.0"
                }
            )
            defaultValue = "net9.0"
            replaces     = "net9.0"
        }
    }
    primaryOutputs      = @(
        @{
            path = "MeshWeaverApp1.TodoTest.csproj"
        }
    )
} | ConvertTo-Json -Depth 10

$testTemplate | Out-File -FilePath "$OutputPath\MeshWeaverApp1.TodoTest\.template.config\template.json" -Encoding utf8

# Create combined solution template
New-Item -ItemType Directory -Path "$OutputPath\.template.config" -Force | Out-Null
$solutionTemplate = @{
    '$schema'           = "http://json.schemastore.org/template"
    author              = "Systemorph"
    classifications     = @("Solution", "MeshWeaver", "Web", "Portal")
    name                = "MeshWeaver Portal with Todo Application"
    identity            = "MeshWeaver.PortalSolution.CSharp"
    groupIdentity       = "MeshWeaver.PortalSolution"
    shortName           = "meshweaver-solution"
    tags                = @{
        language = "C#"
        type     = "solution"
    }
    sourceName          = "MeshWeaverApp1"
    preferNameDirectory = $true
    symbols             = @{
        Framework = @{
            type         = "parameter"
            description  = "The target framework for the project."
            datatype     = "choice"
            choices      = @(
                @{
                    choice      = "net9.0"
                    description = ".NET 9.0"
                }
            )
            defaultValue = "net9.0"
            replaces     = "net9.0"
        }
    }
    primaryOutputs      = @(
        @{
            path = "MeshWeaverApp1.Portal/MeshWeaverApp1.Portal.csproj"
        },
        @{
            path = "MeshWeaverApp1.Todo/MeshWeaverApp1.Todo.csproj"
        },
        @{
            path = "MeshWeaverApp1.Todo.AI/MeshWeaverApp1.Todo.AI.csproj"
        },
        @{
            path = "MeshWeaverApp1.TodoTest/MeshWeaverApp1.TodoTest.csproj"
        }
    )
    postActions         = @(
        @{
            description        = "Restore NuGet packages required by this project."
            manualInstructions = @(
                @{
                    text = "Run 'dotnet restore'"
                }
            )
            actionId           = "210D431B-A78B-4D2F-B762-4ED3E3EA9025"
            continueOnError    = $true
        }
    )
} | ConvertTo-Json -Depth 10

$solutionTemplate | Out-File -FilePath "$OutputPath\.template.config\template.json" -Encoding utf8

# Create solution file
$solutionContent = @"
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
"@

$solutionContent | Out-File -FilePath "$OutputPath\MeshWeaverApp1.sln" -Encoding utf8

# Create README.md
$readmeContent = @"
# MeshWeaver Portal Solution Template

This template creates a complete MeshWeaver portal application with a Todo module example.

## Projects Included

- **MeshWeaverApp1.Portal**: The main web portal application
- **MeshWeaverApp1.Todo**: A Todo module demonstrating MeshWeaver data management and layout areas
- **MeshWeaverApp1.Todo.AI**: AI agent for Todo management with natural language processing
- **MeshWeaverApp1.TodoTest**: Unit tests for the Todo module

## Getting Started

1. Install the template package: ``dotnet new install MeshWeaver.ProjectTemplates``
2. Create a new project using: ``dotnet new meshweaver-solution -n MyApp``
3. Navigate to the created directory: ``cd MyApp``
4. Run the application: ``dotnet run --project MyApp.Portal``

## Features

- Complete portal infrastructure
- Reactive data management
- Layout areas with real-time updates
- AI-powered Todo assistant with natural language processing
- Comprehensive testing setup
- Sample Todo application with CRUD operations

## Individual Templates

You can also create individual projects:

- Portal only: ``dotnet new meshweaver-portal -n MyPortal``
- Todo library only: ``dotnet new meshweaver-todo -n MyTodo``
- Todo AI library only: ``dotnet new meshweaver-todo-ai -n MyTodoAI``
- Test project only: ``dotnet new meshweaver-test -n MyTests``

## MeshWeaver Version

This template is based on MeshWeaver version $Version.

## Development

To run the application locally:

1. Navigate to the Portal project: ``cd MeshWeaverApp1.Portal``
2. Run: ``dotnet run``
3. Open browser to: ``https://localhost:5001``

The Todo application will be available in the portal with sample data.
"@

$readmeContent | Out-File -FilePath "$OutputPath\README.md" -Encoding utf8

Write-Host "Template creation completed successfully!" -ForegroundColor Green
Write-Host "Output directory: $OutputPath" -ForegroundColor Cyan
Write-Host ""
Write-Host "To create NuGet template package, run:" -ForegroundColor Yellow
Write-Host "  dotnet pack templates/packaging/MeshWeaver.ProjectTemplates.csproj -o nupkg" -ForegroundColor Gray
Write-Host ""
Write-Host "To test locally before packaging:" -ForegroundColor Yellow
Write-Host "  cd $OutputPath" -ForegroundColor Gray
Write-Host "  dotnet new install ." -ForegroundColor Gray
Write-Host "  dotnet new meshweaver-solution -n TestApp" -ForegroundColor Gray
