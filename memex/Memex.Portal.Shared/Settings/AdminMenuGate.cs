using System.Reactive.Linq;
using System.Threading.Channels;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Gate for the platform-wide Admin menu (the GlobalSettings area). A tab is shown only to a viewer
/// holding root-level <see cref="Permission.All"/> — the same bar as the "Global Administration" tab.
/// Bridges the <c>IObservable</c> permission check to <c>IAsyncEnumerable</c> via a Channel (no
/// <c>.ToTask()</c> on a hub round-trip — see AsynchronousCalls.md).
/// </summary>
internal static class AdminMenuGate
{
    public static async Task<bool> IsRootAdminAsync(LayoutAreaHost host)
    {
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewerId = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;
        if (string.IsNullOrEmpty(viewerId))
            return false;

        var channel = Channel.CreateBounded<Permission>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        using var sub = host.Hub.GetEffectivePermissions("", viewerId)
            .FirstAsync()
            .Subscribe(
                perm => { channel.Writer.TryWrite(perm); channel.Writer.TryComplete(); },
                _ => channel.Writer.TryComplete(),
                () => channel.Writer.TryComplete());

        await foreach (var perm in channel.Reader.ReadAllAsync())
            return perm.HasFlag(Permission.All);
        return false;
    }
}
