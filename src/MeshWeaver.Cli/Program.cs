using System.Text;
using MeshWeaver.Cli;

Console.OutputEncoding = Encoding.UTF8;

// The command tree lives in CliApp: the built-ins are compiled in, the mesh commands are generated
// from the connected instance's MCP tool catalog (see DynamicCommands for the mapping rule).
return await CliApp.RunAsync(args, CliHost.FromConsole());
