#!/usr/bin/env pwsh

param(
    [string]$Version = "2.2.0-rc5",
    [string]$OutputPath = ".\dist\templates",
    [bool]$PackageTemplates = $true
)

$ErrorActionPreference = "Stop"

Write-Host "Creating MeshWeaver Project Templates v$Version" -ForegroundColor Green

# Clean output directory
if (Test-Path $OutputPath) {
    Remove-Item -Path $OutputPath -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null

# Define projects to copy
$projects = @(
    @{
        Source = "templates\MeshWeaverApp1.Portal"
        Target = "$OutputPath\MeshWeaverApp1.Portal"
        Type   = "Portal"
    },
    @{
        Source = "templates\MeshWeaverApp1.Todo"
        Target = "$OutputPath\MeshWeaverApp1.Todo"
        Type   = "Library"
    },
    @{
        Source = "templates\MeshWeaverApp1.TodoTest"
        Target = "$OutputPath\MeshWeaverApp1.TodoTest"
        Type   = "Test"
    }
)

# Copy projects
foreach ($project in $projects) {
    Write-Host "Copying $($project.Source) to $($project.Target)..." -ForegroundColor Yellow
    
    if (!(Test-Path $project.Source)) {
        Write-Error "Source path $($project.Source) does not exist"
        continue
    }
    
    # Copy entire directory structure
    Copy-Item -Path $project.Source -Destination $project.Target -Recurse -Force
    
    # Update project references to package references
    $csprojFile = Get-ChildItem -Path $project.Target -Filter "*.csproj" | Select-Object -First 1
    
    if ($csprojFile) {
        Write-Host "Updating project file: $($csprojFile.FullName)" -ForegroundColor Cyan
        
        $content = Get-Content $csprojFile.FullName
        
        # Replace project references with package references based on project type
        switch ($project.Type) {
            "Portal" {
                $content = $content -replace '<ProjectReference Include="\.\.\\\.\.\\src\\([^\\]+)\\([^\\]+)\.csproj" />', '<PackageReference Include="$2" Version="' + $Version + '" />'
                $content = $content -replace '<ProjectReference Include="\.\.\\MeshWeaverApp1\.Todo\\MeshWeaverApp1\.Todo\.csproj" />', '<ProjectReference Include="..\MeshWeaverApp1.Todo\MeshWeaverApp1.Todo.csproj" />'
            }
            "Library" {
                $content = $content -replace '<ProjectReference Include="\.\.\\\.\.\\src\\([^\\]+)\\([^\\]+)\.csproj" />', '<PackageReference Include="$2" Version="' + $Version + '" />'
            }
            "Test" {
                $content = $content -replace '<ProjectReference Include="\.\.\\\.\.\\test\\([^\\]+)\\([^\\]+)\.csproj" />', '<PackageReference Include="$2" Version="' + $Version + '" />'
                $content = $content -replace '<ProjectReference Include="\.\.\\MeshWeaverApp1\.Todo\\MeshWeaverApp1\.Todo\.csproj" />', '<ProjectReference Include="..\MeshWeaverApp1.Todo\MeshWeaverApp1.Todo.csproj" />'
                
                # Add ItemGroup for appsettings.json if not already present
                if ($content -notmatch '<None Update="appsettings\.json">') {
                    $itemGroupToAdd = @"
  
  <ItemGroup>
    <None Update="appsettings.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
"@
                    $content = $content -replace '</Project>', ($itemGroupToAdd + "`n</Project>")
                }
            }
        }
        
        Set-Content -Path $csprojFile.FullName -Value $content
    }
}

# Create template.json files for each project
Write-Host "Creating template metadata files..." -ForegroundColor Yellow

# Portal template.json
$portalTemplateJson = @{
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
        Framework   = @{
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
        skipRestore = @{
            type         = "parameter"
            datatype     = "bool"
            description  = "If specified, skips the automatic restore of the project on create."
            defaultValue = "false"
        }
    }
    primaryOutputs      = @(
        @{
            path = "MeshWeaverApp1.Portal.csproj"
        }
    )
    postActions         = @(
        @{
            condition          = "(!skipRestore)"
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

$portalTemplateJson | Out-File -FilePath "$OutputPath\MeshWeaverApp1.Portal\.template.config\template.json" -Encoding utf8
New-Item -ItemType Directory -Path "$OutputPath\MeshWeaverApp1.Portal\.template.config" -Force | Out-Null
$portalTemplateJson | Out-File -FilePath "$OutputPath\MeshWeaverApp1.Portal\.template.config\template.json" -Encoding utf8

# Todo Library template.json
$todoTemplateJson = @{
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

New-Item -ItemType Directory -Path "$OutputPath\MeshWeaverApp1.Todo\.template.config" -Force | Out-Null
$todoTemplateJson | Out-File -FilePath "$OutputPath\MeshWeaverApp1.Todo\.template.config\template.json" -Encoding utf8

# Todo Test template.json
$testTemplateJson = @{
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

New-Item -ItemType Directory -Path "$OutputPath\MeshWeaverApp1.TodoTest\.template.config" -Force | Out-Null
$testTemplateJson | Out-File -FilePath "$OutputPath\MeshWeaverApp1.TodoTest\.template.config\template.json" -Encoding utf8

if ($PackageTemplates) {
    # Create a combined template package
    Write-Host "Creating combined template package..." -ForegroundColor Yellow
    
    $packageTemplateJson = @{
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
    
    New-Item -ItemType Directory -Path "$OutputPath\.template.config" -Force | Out-Null
    $packageTemplateJson | Out-File -FilePath "$OutputPath\.template.config\template.json" -Encoding utf8
    
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
- **MeshWeaverApp1.TodoTest**: Unit tests for the Todo module

## Getting Started

1. Install the template package
2. Create a new project using: `dotnet new meshweaver-solution -n MyApp`
3. Navigate to the created directory: `cd MyApp`
4. Run the application: `dotnet run --project MyApp.Portal`

## Features

- Complete portal infrastructure
- Reactive data management
- Layout areas with real-time updates
- Comprehensive testing setup
- Sample Todo application with CRUD operations

## MeshWeaver Version

This template is based on MeshWeaver version $Version.
"@
    
    $readmeContent | Out-File -FilePath "$OutputPath\README.md" -Encoding utf8
}

Write-Host "Template creation completed successfully!" -ForegroundColor Green
Write-Host "Output directory: $OutputPath" -ForegroundColor Cyan

if ($PackageTemplates) {
    Write-Host ""
    Write-Host "To create NuGet template packages, run:" -ForegroundColor Yellow
    Write-Host "  dotnet pack templates/MeshWeaver.ProjectTemplates.csproj" -ForegroundColor Gray
}
