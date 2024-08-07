
using Microsoft.DotNet.Interactive.Commands;

namespace MeshWeaver.CSharp.Kernel;

public static class KernelExtensions
{
    public static KernelCommand GetRootCommand(this KernelCommand command)
    {
        if (command.Parent is null)
            return command;
        return GetRootCommand(command.Parent);
    }
}