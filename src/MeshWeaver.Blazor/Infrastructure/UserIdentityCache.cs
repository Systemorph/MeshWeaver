using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Infrastructure;

/// <summary>
/// Hot, in-process index of mesh <c>User</c> nodes by email. Subscribes to
/// <see cref="IMeshService.ObserveQuery{T}"/> at construction and maintains a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> snapshot so the user-context
/// middleware can resolve <c>email → mesh user</c> synchronously without ever
/// awaiting on a hub-touching observable (which would deadlock).
/// </summary>
public sealed class UserIdentityCache : IDisposable
{
    private readonly ConcurrentDictionary<string, MeshNode> _byEmail =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IDisposable _subscription;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<UserIdentityCache> _logger;

    public UserIdentityCache(IMeshService mesh, IMessageHub hub, ILogger<UserIdentityCache> logger)
    {
        _jsonOptions = hub.JsonSerializerOptions;
        _logger = logger;
        _subscription = mesh
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("nodeType:User namespace:User"))
            .Subscribe(
                Apply,
                ex => _logger.LogWarning(ex, "UserIdentityCache subscription failed"));
    }

    private void Apply(QueryResultChange<MeshNode> change)
    {
        switch (change.ChangeType)
        {
            case QueryChangeType.Initial:
            case QueryChangeType.Reset:
                _byEmail.Clear();
                foreach (var node in change.Items)
                    if (TryGetEmail(node, out var email))
                        _byEmail[email] = node;
                break;
            case QueryChangeType.Added:
            case QueryChangeType.Updated:
                foreach (var node in change.Items)
                    if (TryGetEmail(node, out var email))
                        _byEmail[email] = node;
                break;
            case QueryChangeType.Removed:
                foreach (var node in change.Items)
                    if (TryGetEmail(node, out var email))
                        _byEmail.TryRemove(email, out _);
                break;
        }
    }

    /// <summary>
    /// Synchronous lookup: returns the mesh <c>User</c> node whose
    /// <c>content.email</c> matches, or <c>null</c> if no matching node has
    /// reached the cache yet.
    /// </summary>
    public MeshNode? TryGetByEmail(string email)
        => string.IsNullOrEmpty(email) ? null
            : _byEmail.TryGetValue(email, out var node) ? node : null;

    private bool TryGetEmail(MeshNode node, out string email)
    {
        email = "";
        if (node.Content is null) return false;
        try
        {
            string? value = null;
            if (node.Content is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Object
                    && je.TryGetProperty("email", out var emailProp)
                    && emailProp.ValueKind == JsonValueKind.String)
                    value = emailProp.GetString();
            }
            else
            {
                var prop = node.Content.GetType().GetProperty("Email")
                           ?? node.Content.GetType().GetProperty("email");
                value = prop?.GetValue(node.Content) as string;
            }
            if (!string.IsNullOrEmpty(value))
            {
                email = value;
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UserIdentityCache failed to extract email for {Path}", node.Path);
        }
        return false;
    }

    public void Dispose() => _subscription.Dispose();
}
