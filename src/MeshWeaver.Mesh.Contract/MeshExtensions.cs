using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Contract
{
    public static class MeshExtensions
    {
        private static Func<MeshConfiguration, MeshConfiguration> GetLambda(
            this MessageHubConfiguration config
        )
        {
            return config.Get<Func<MeshConfiguration, MeshConfiguration>>()
                   ?? (x => x);
        }

        public static MeshConfiguration GetMeshContext(this MessageHubConfiguration config)
        {
            var meshConf= config.GetLambda();
            return meshConf.Invoke(new());
        }

    }
}
