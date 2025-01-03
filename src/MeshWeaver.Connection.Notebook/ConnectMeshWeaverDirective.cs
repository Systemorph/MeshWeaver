using System.Reactive.Subjects;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Connection;
using Microsoft.DotNet.Interactive.Directives;

namespace MeshWeaver.Connection.Notebook
{
    public class ConnectMeshWeaverDirective : ConnectKernelDirective<ConnectMeshWeaverKernel>
    {
        private record LanguageDescriptor(string Name, string DisplayName, string LanguageName, string LanguageVersion);

        private static readonly Dictionary<string, LanguageDescriptor> LanguageDescriptors =
            new LanguageDescriptor[]
                {
                    new("csharp", "csharp", "C#", "12.0")
                }
                .ToDictionary(x => x.Name);
        private KernelDirectiveParameter UrlParameter { get; } =
            new("--url", description: "The url of the SignalR end point for the MeshWeaver kernel. Typically https://<mesh-url>/kernel.")
            {
                AllowImplicitName = true,
                Required = true,
            };
        private KernelDirectiveParameter LanguageParameter { get; } =
            new("--language", description: "The language of the MeshWeaver kernel. Default is C#")
            {
                AllowImplicitName = false,
                Required = false,
            };

        public ConnectMeshWeaverDirective() : base("mesh", "Connects to a Mesh Weaver instance")
        {
            Parameters.Add(UrlParameter);
            Parameters.Add(LanguageParameter);
        }

        public override async Task<IEnumerable<Kernel>> ConnectKernelsAsync(
            ConnectMeshWeaverKernel connectCommand, 
            KernelInvocationContext context)
        {
            if (string.IsNullOrWhiteSpace(connectCommand.Url))
            {
                throw new ArgumentException("Provide a valid Connection string");
            }

            var connection = new HubConnectionBuilder()
                .WithUrl(connectCommand.Url, ConnectionSettings.HttpConnectionOptions)
                .WithAutomaticReconnect()
                .Build();
            var kernelId = $"{Guid.NewGuid().ToString()}";

            async Task ConnectAsync(object exception = null)
            {
                try
                {
                    var connected =
                        await connection.InvokeAsync<bool>(
                            "Connect",
                            kernelId);

                    if (!connected)
                        throw new MeshWeaverKernelException("Couldn't connect.");

                }
                catch (Exception ex)
                {
                    //logger.LogError("Unable connecting SignalR connection for {Address} :\n{Exception}", clientId, ex);
                    throw;
                }
                // Your callback logic here
                Console.WriteLine("Reconnecting...");

            }

            connection.Reconnecting += ConnectAsync;

            var subject = new Subject<string>();
            connection.On<string>("kernelEvents", e => subject.OnNext(e));
            var receiver = KernelCommandAndEventReceiver.FromObservable(subject);

            var kernel = new ProxyKernel( 
                connectCommand.ConnectedKernelName, 
                new KernelCommandAndEventSignalRHubConnectionSender(connection, kernelId), 
                receiver,
                new Uri($"kernel://mesh/csharp")
            );
            connection.Closed += async (error) =>
            {
                connection.Reconnecting -= ConnectAsync;
            };
            kernel.RegisterForDisposal(() =>
            {
                connection.InvokeAsync<bool>("DisposeKernel").Wait();
                connection.StopAsync().Wait();
            });

            var language = connectCommand.Language == null
                ? LanguageDescriptors.Values.First()
                : LanguageDescriptors.GetValueOrDefault(connectCommand.Language) ??
                  throw new ArgumentException($"Unknown language: {connectCommand.Language}");

            var kernelInfo = kernel.KernelInfo;
            kernelInfo.DisplayName = $"Mesh - {language.DisplayName}";
            kernelInfo.LanguageName = language.LanguageName;
            kernelInfo.LanguageVersion = language.LanguageVersion;
            kernelInfo.RemoteUri = new Uri($"kernel://mesh/{language.Name}");
            kernelInfo.Description = $"Mesh Weaver connection in {language.DisplayName}";

            await connection.StartAsync();
            await ConnectAsync();
            return [kernel];
        }

        
        public static void Install()
        {
            if(Kernel.Current.RootKernel is CompositeKernel composite)
                composite.AddConnectDirective(new ConnectMeshWeaverDirective());
        }
    }
}
