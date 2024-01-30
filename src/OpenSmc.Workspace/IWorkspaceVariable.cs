using Microsoft.Extensions.DependencyInjection;
using System;
using OpenSmc.Scheduling;
using OpenSmc.DataSource.Api;

namespace OpenSmc.Workspace
{
    public interface IWorkspaceVariable : IWorkspace
    {
        IWorkspace CreateNew();
    }

    public class WorkspaceVariable : Workspace, IWorkspaceVariable 
    {
        private readonly IServiceProvider serviceProvider;

        public WorkspaceVariable(IWorkspaceStorage workspaceStorage, IDataScheduler dataScheduler, IPartitionVariable partition, IServiceProvider serviceProvider) : base(workspaceStorage, dataScheduler, partition)
        {
            this.serviceProvider = serviceProvider;
        }

        public IWorkspace CreateNew()
        {
            return serviceProvider.GetService<IWorkspace>();
        }
    }
}
