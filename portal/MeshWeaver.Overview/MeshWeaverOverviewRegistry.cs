using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Overview
{
    public static class MeshWeaverOverviewRegistry
    {
        public static MessageHubConfiguration AddMeshWeaverOverview(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout);
}
