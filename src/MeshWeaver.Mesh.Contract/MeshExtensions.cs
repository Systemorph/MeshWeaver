using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Contract
{
    public static class MeshExtensions
    {
        private static IReadOnlyCollection<Func<MeshConfiguration, MeshConfiguration>> GetMeshConfigurations(
            this MessageHubConfiguration config
        )
        {
            return config.Get<IReadOnlyCollection<Func<MeshConfiguration, MeshConfiguration>>>()
                   ?? [];
        }

        public static MeshConfiguration GetMeshContext(this MessageHubConfiguration config)
        {
            var meshConf= config.GetMeshConfigurations();
            return meshConf.Aggregate(new MeshConfiguration(), (x,y) =>y.Invoke(x));
        }

    }
}
