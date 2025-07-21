# MeshWeaver Project Templates

This NuGet package provides project templates for creating MeshWeaver applications.

## Installation

```bash
dotnet new install MeshWeaver.ProjectTemplates
```

## Available Templates

- `meshweaver-solution` - Complete portal solution with Todo module and AI agent
- `meshweaver-portal` - Portal application only
- `meshweaver-todo` - Todo library module only
- `meshweaver-todo-ai` - Todo AI agent library only
- `meshweaver-test` - Test project with MeshWeaver testing infrastructure

## Usage

Create a new MeshWeaver solution:

```bash
dotnet new meshweaver-solution -n MyApp
cd MyApp
dotnet restore --source /path/to/meshweaver/nupkg
dotnet run --project MyApp.Portal
```

## Features

- Blazor portal application
- Todo module with CRUD operations
- AI-powered Todo assistant with natural language processing
- Comprehensive testing setup
- Real-time data synchronization
- Layout areas for responsive UI

## Package Source

This template references MeshWeaver packages version 2.2.0-rc6. If installing from a local package source, ensure you have the required MeshWeaver packages available or add the package source:

```bash
dotnet restore --source /path/to/meshweaver/nupkg --source https://api.nuget.org/v3/index.json
```
