# MeshWeaver.Kernel.Hub

## Overview
MeshWeaver.Kernel.Hub provides a message hub plugin for a dotnet interactive kernel. It allows notebooks to execute code against remote kernels distributed across the mesh.

## Usage

### Basic Configuration
```csharp
var builder = WebApplication.CreateBuilder(args);

// Configure mesh with kernel support
builder.ConfigureMesh(builder => 
    builder
        .AddKernel()
        .ConfigureMesh(config => config.AddMeshNodes(/* your nodes */))
);

var app = builder.Build();

```

### Code Execution Examples

#### Basic Code Execution
```csharp
// Execute code through the kernel
var command = new SubmitCode("Console.WriteLine(\"Hello World\");");
client.Post(
    new KernelCommandEnvelope(KernelCommandEnvelope.Serialize(
        KernelCommandEnvelope.Create(command)))
    {
        IFrameUrl = "http://localhost/area"
    },
    o => o.WithTarget(new KernelAddress())
);
```

#### Interactive Data Processing
```csharp
// Example of interactive calculator with UI updates
const string Code = @"
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using static MeshWeaver.Layout.Controls;

record Calculator(double Summand1, double Summand2);
static object CalculatorSum(Calculator c) => 
    Markdown($""**Sum**: {c.Summand1 + c.Summand2}"");

Mesh.Edit(new Calculator(1,2), CalculatorSum)
";

client.Post(
    new SubmitCodeRequest(Code) { Id = "Area" },
    o => o.WithTarget(new KernelAddress())
);
```

## Features
- Remote kernel execution through mesh
- Interactive data processing
- Real-time UI updates
- State management across kernel sessions
- Integration with MeshWeaver layout system

## Integration
- Works with [.NET Interactive](https://github.com/dotnet/interactive)
- Compatible with both monolithic and Orleans hosting
- Supports mesh message routing
- Integrates with MeshWeaver layout system

## See Also
- [.NET Interactive Documentation](https://github.com/dotnet/interactive) - Learn more about .NET Interactive
- [Main MeshWeaver Documentation](../../Readme.md) - More about MeshWeaver hosting options
