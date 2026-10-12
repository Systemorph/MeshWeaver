using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#6542 — <b>a root container whose build callback throws is disposed, with every shared
/// instance the callbacks before it built.</b> <see cref="ServiceProviderExtensions.SetupModules"/> runs
/// each <see cref="IRootContainerBuilt"/> after the root container is built and before it returns it, so
/// a callback that throws leaves no caller holding the root: without the dispose, the singletons already
/// built live on, undisposed, for the rest of the process.
/// </summary>
public sealed class ARootWhoseBuildCallbackThrowsIsDisposedTest
{
    [Fact]
    public void ACallbackThatThrows_DisposesTheRoot_AndWhatWasAlreadyBuilt()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Tracked>();
        var builds = new BuildsTracked();
        services.AddSingleton<IRootContainerBuilt>(builds);
        services.AddSingleton<IRootContainerBuilt>(new Throws());

        var thrown = Record.Exception(() => services.SetupModules(null));

        thrown.Should().BeOfType<InvalidOperationException>("the callback's exception reaches the caller unchanged");
        builds.Instance.Should().NotBeNull("the first callback built the singleton before the second threw");
        builds.Instance!.Disposed.Should().BeTrue(
            "nobody else holds the root, so SetupModules disposes it — and the singletons it already built");
    }

    private sealed class Tracked : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class BuildsTracked : IRootContainerBuilt
    {
        public Tracked? Instance { get; private set; }
        public void Built(IServiceProvider root) => Instance = root.GetRequiredService<Tracked>();
    }

    private sealed class Throws : IRootContainerBuilt
    {
        public void Built(IServiceProvider root) =>
            throw new InvalidOperationException("a build callback that fails");
    }
}
