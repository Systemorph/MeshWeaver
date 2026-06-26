using System.Reactive.Linq;
using System.Security.Claims;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Memex.Portal.Shared.Authentication;

[ApiController]
[Route("dev")]
public class DevAuthController : ControllerBase
{
    private readonly IMeshService _meshQuery;
    private readonly UserOnboardingService _onboarding;
    private readonly bool _devLoginEnabled;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DevAuthController(IMeshService meshQuery, UserOnboardingService onboarding, IConfiguration configuration)
    {
        _meshQuery = meshQuery;
        _onboarding = onboarding;
        // DevLogin is forced OFF in prod (Memex.Portal.Distributed/Program.cs); this gate
        // ensures the self-provisioning below can NEVER auto-create users in production.
        _devLoginEnabled = configuration["Authentication:EnableDevLogin"] == "true";
    }

    /// <summary>
    /// Signs in as the specified person.
    /// </summary>
    [HttpPost("signin")]
    public async Task<IActionResult> Login([FromForm] string personId, [FromForm] string? returnUrl)
    {
        // Resolve the dev user across BOTH user-node shapes (so dev login works on the monolith
        // AND the distributed portal). The monolith / AddSampleUsers seed the legacy `User/{id}`
        // node (namespace "User"); the distributed portal's UserOnboardingService writes the
        // partition-root `{id}` node (namespace "", nodeType "User") and no longer mirrors it under
        // `User/`. Query nodeType:User and match by id (case-insensitive) or the legacy path.
        // TODO(persistence-cull): framework boundary — review whether this should
        // route through UserIdentityCache (sync) instead of awaiting the observable.
        var change = await _meshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery("nodeType:User"))
            .FirstAsync();
        var node = change.Items.FirstOrDefault(n =>
            string.Equals(n.Id, personId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(n.Path, $"User/{personId}", StringComparison.OrdinalIgnoreCase));

        // DevLogin self-provisions (dev only — gated on EnableDevLogin, forced off in prod).
        // A personId with no User node yet is onboarded on first signin via the SAME reactive
        // dual-write the Entra onboarding flow runs (UserOnboardingService.CreateUser +
        // GrantSelfAdmin), writing the partition-root `{id}` User node the lookup above
        // matches. So a throwaway DevLogin portal (the e2e portal) works for ANY user with no
        // separate seed step; without it DevLogin 400s on a fresh DB that seeds no users.
        if ((node?.NodeType != "User" || node.Content == null) && _devLoginEnabled)
            node = await ProvisionDevUser(personId);

        if (node?.NodeType != "User" || node.Content == null)
        {
            return BadRequest("Person not found");
        }

        var person = ExtractPersonInfo(node.Id, node.Content);
        if (person == null)
        {
            return BadRequest("Could not extract person info");
        }

        // Create claims: username is the node ID, email in content.
        // 🚨 preferred_username MUST be the username (node Id), NOT the email.
        // UserContextMiddleware.ExtractUserContext takes ObjectId from
        // preferred_username first; if that's the email, every downstream
        // route targets `<email>` instead of the user's partition and the
        // portal renders "No node found at 'rbuergi@systemorph.com'".
        // ApiTokenAuthenticationHandler already puts the username here — keep
        // the dev login consistent so the user's partition (path = node Id)
        // is the resolved home.
        var email = person.Email ?? "";
        var username = node.Id;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, username),
            new(ClaimTypes.Name, username),
            new("name", username),
            new("preferred_username", username),
        };

        if (!string.IsNullOrEmpty(email))
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
            claims.Add(new Claim("email", email));
        }

        if (!string.IsNullOrEmpty(person.Role))
        {
            claims.Add(new Claim(ClaimTypes.Role, person.Role));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
            });

        return Redirect(returnUrl ?? "/");
    }

    /// <summary>
    /// Onboards a brand-new DevLogin user on first signin (dev only): creates the per-user
    /// partition-root User node and grants self-Admin, reusing the same reactive
    /// <see cref="UserOnboardingService"/> dual-write as the Entra onboarding flow. Returns
    /// the partition-root User node the sign-in claims are then built from. The grants run as
    /// System (the brand-new partition has no caller identity yet — see UserOnboardingService).
    /// </summary>
    private async Task<MeshNode> ProvisionDevUser(string personId)
    {
        var request = new UserOnboardingRequest(
            Username: personId,
            Email: $"{personId.ToLowerInvariant()}@dev.local",
            FullName: personId,
            Role: Role.Admin.Id);
        var userNode = await _onboarding.CreateUser(request).FirstAsync();
        await _onboarding.GrantSelfAdmin(personId).FirstAsync();
        return userNode;
    }

    /// <summary>
    /// Signs out the current user.
    /// </summary>
    [HttpPost("logout")]
    [HttpGet("logout")]
    public async Task<IActionResult> Logout([FromQuery] string? returnUrl)
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect(returnUrl ?? "/");
    }

    private static PersonInfo? ExtractPersonInfo(string id, object content)
    {
        try
        {
            // Content is typically a JsonElement or a dynamic object
            if (content is JsonElement jsonElement)
            {
                return new PersonInfo
                {
                    Id = id,
                    Name = jsonElement.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? id : id,
                    Email = jsonElement.TryGetProperty("email", out var emailProp) ? emailProp.GetString() : null,
                    Role = jsonElement.TryGetProperty("role", out var roleProp) ? roleProp.GetString() : null,
                    Avatar = jsonElement.TryGetProperty("avatar", out var avatarProp) ? avatarProp.GetString() : null
                };
            }

            // Try to serialize and deserialize to handle other object types
            var json = JsonSerializer.Serialize(content, JsonOptions);
            var parsed = JsonSerializer.Deserialize<JsonElement>(json);

            return new PersonInfo
            {
                Id = id,
                Name = parsed.TryGetProperty("name", out var nameProp2) ? nameProp2.GetString() ?? id : id,
                Email = parsed.TryGetProperty("email", out var emailProp2) ? emailProp2.GetString() : null,
                Role = parsed.TryGetProperty("role", out var roleProp2) ? roleProp2.GetString() : null,
                Avatar = parsed.TryGetProperty("avatar", out var avatarProp2) ? avatarProp2.GetString() : null
            };
        }
        catch
        {
            return new PersonInfo { Id = id, Name = id };
        }
    }
}

/// <summary>
/// Simplified person info for the login UI.
/// </summary>
public record PersonInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Email { get; init; }
    public string? Role { get; init; }
    public string? Avatar { get; init; }
}
