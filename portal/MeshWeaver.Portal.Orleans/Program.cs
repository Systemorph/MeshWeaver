using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Orleans.Client;
using MeshWeaver.Hosting.Orleans.Server;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Portal.ServiceDefaults;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.AddKeyedAzureTableClient(StorageProviders.MeshCatalog);
builder.AddKeyedAzureTableClient(StorageProviders.Activity);
builder.AddKeyedRedisClient(StorageProviders.Redis);
var address = new OrleansAddress();


var currentDirectory = Directory.GetCurrentDirectory();
var deployDirectory = Path.GetFullPath(Path.Combine(currentDirectory, "..", "..", "..", "..", "..", "deploy"));
var directories = Directory.GetDirectories(deployDirectory);
var modules = directories.Select(d => Path.Combine(d, $"{Path.GetFileName(d)}.dll")).ToArray();

// Now you can use the solutionDirectory and deployFolder variables as needed


builder.
    UseMeshWeaver(address, conf =>
        conf
            .UseOrleansMeshServer()
            .ConfigureMesh(mesh => mesh.InstallAssemblies(modules))
            );

var app = builder.Build();

app.Run();
