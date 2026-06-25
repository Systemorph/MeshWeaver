using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Client.Services;

/// <summary>
/// Interactive onboarding for the single device user — the FRAMEWORK path, not a hand-rolled grant.
/// Creating the partition-root <c>User</c> node (namespace "") triggers the framework's own handlers:
/// <c>OwnsPartitionProvisioningValidator</c> provisions the <c>device-user</c> partition schema and the
/// User post-create handler grants self-Admin in <c>device-user/_Access</c>. We then add the
/// <c>Admin/_Access</c> grant so the device owner is the <b>global admin of this instance</b> (the
/// sanctioned shape; <c>hub.IsGlobalAdmin()</c>), never a root <c>_Access</c> data-superuser grant.
/// </summary>
public sealed class DeviceOnboarding(IMessageHub hub)
{
    public const string DeviceUserId = "device-user";

    /// <summary>True once the device user's User node exists (first-boot detection).</summary>
    public IObservable<bool> IsOnboarded() =>
        hub.GetWorkspace()
            .GetQuery("onboard-check", $"nodeType:User content.email:{DeviceUserId}@local limit:1")
            .Take(1).Timeout(TimeSpan.FromSeconds(15))
            .Select(existing => existing.Any())
            .Catch<bool, Exception>(_ => Observable.Return(false));

    /// <summary>
    /// Onboard the device user with the entered profile, making them global admin of this instance.
    /// Runs as System (a brand-new partition root is owned by nobody yet).
    /// </summary>
    public IObservable<Unit> Onboard(string fullName, string? bio, string? role)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var name = string.IsNullOrWhiteSpace(fullName) ? Environment.UserName : fullName.Trim();

        return Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => meshService.CreateNode(new MeshNode(DeviceUserId)   // partition root → provisions schema + self-Admin
                {
                    NodeType = "User",
                    Name = name,
                    State = MeshNodeState.Active,
                    Content = new User
                    {
                        FullName = name,
                        Email = $"{DeviceUserId}@local",
                        Bio = string.IsNullOrWhiteSpace(bio) ? null : bio.Trim(),
                        Role = string.IsNullOrWhiteSpace(role) ? null : role.Trim(),
                    },
                })
                // Global admin of this instance — Admin/_Access, MainNode="" (the sanctioned shape).
                .SelectMany(_ => meshService.CreateNode(new MeshNode($"{DeviceUserId}_Access", "Admin/_Access")
                {
                    NodeType = "AccessAssignment",
                    Name = $"{name} — Admin",
                    MainNode = "",
                    Content = new AccessAssignment
                    {
                        AccessObject = DeviceUserId,
                        DisplayName = name,
                        Roles = [new RoleAssignment { Role = "Admin" }],
                    },
                }))
                .Select(_ => Unit.Default));
    }

    /// <summary>The macOS full name (e.g. "Roland Bürgi"), falling back to the OS short username.</summary>
    public static string FullNameGuess()
    {
#if MACCATALYST || IOS
        try
        {
            var ptr = NSFullUserName();
            var full = ObjCRuntime.Runtime.GetNSObject<Foundation.NSString>(ptr)?.ToString();
            if (!string.IsNullOrWhiteSpace(full)) return full!;
        }
        catch { /* fall through to the OS short name */ }
#endif
        return Environment.UserName;
    }

#if MACCATALYST || IOS
    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/Foundation.framework/Foundation")]
    private static extern IntPtr NSFullUserName();
#endif
}
