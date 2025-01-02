namespace MeshWeaver.Hosting.SignalR
{
    public interface IKernelService
    {
        Task<string> GetKernelIdAsync(string clientId);

        Task SubmitCommandAsync(string clientId, string commandEnvelope);
    }
}
