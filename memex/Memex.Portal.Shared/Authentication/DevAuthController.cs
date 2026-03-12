using System.Security.Claims;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Memex.Portal.Shared.Authentication;

[ApiController]
[Route("dev")]
public class DevAuthController : ControllerBase
{
    private readonly IMeshService _meshQuery;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DevAuthController(IMeshService meshQuery)
    {
        _meshQuery = meshQuery;
    }

    /// <summary>
    /// Signs in as the specified person.
    /// </summary>
    [HttpPost("signin")]
    public async Task<IActionResult> Login([FromForm] string personId, [FromForm] string? returnUrl)
    {
        // Fetch the person node via IMeshService (bypasses security)
        var node = await _meshQuery.QueryAsync<MeshNode>($"path:User/{personId} scope:self").FirstOrDefaultAsync();
        if (node?.NodeType != "User" || node.Content == null)
        {
            return BadRequest("Person not found");
        }

        var person = ExtractPersonInfo(node.Id, node.Content);
        if (person == null)
        {
            return BadRequest("Could not extract person info");
        }

        // Create claims: username is the node ID, email in content
        var email = person.Email ?? "";
        var username = node.Id;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, username),
            new(ClaimTypes.Name, username),
            new("name", username),
        };

        if (!string.IsNullOrEmpty(email))
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
            claims.Add(new Claim("email", email));
            claims.Add(new Claim("preferred_username", email));
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
