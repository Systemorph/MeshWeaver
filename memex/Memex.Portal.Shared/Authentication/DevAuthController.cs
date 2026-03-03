using System.Security.Claims;
using System.Text.Json;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Memex.Portal.Shared.Authentication;

[ApiController]
[Route("dev")]
public class DevAuthController : ControllerBase
{
    private readonly IPersistenceService _persistence;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DevAuthController(IPersistenceService persistence)
    {
        _persistence = persistence;
    }

    /// <summary>
    /// Gets all available persons for the dev login UI.
    /// </summary>
    [HttpGet("persons")]
    public async Task<ActionResult<List<PersonInfo>>> GetPersons()
    {
        var persons = await GetAllPersonsAsync();
        return Ok(persons.OrderBy(p => p.Name).ToList());
    }

    private async Task<List<PersonInfo>> GetAllPersonsAsync()
    {
        var persons = new List<PersonInfo>();

        await foreach (var node in _persistence.GetDescendantsAsync(null))
        {
            if (node.NodeType == "User" && node.Content != null)
            {
                var person = ExtractPersonInfo(node.Path, node.Content);
                if (person != null)
                {
                    persons.Add(person);
                }
            }
        }

        return persons;
    }

    /// <summary>
    /// Signs in as the specified person.
    /// </summary>
    [HttpPost("signin")]
    public async Task<IActionResult> Login([FromForm] string personId, [FromForm] string? returnUrl)
    {
        // Find the person node
        var node = await _persistence.GetNodeAsync(personId);
        if (node?.NodeType != "User" || node.Content == null)
        {
            return BadRequest("Person not found");
        }

        var person = ExtractPersonInfo(node.Id, node.Content);
        if (person == null)
        {
            return BadRequest("Could not extract person info");
        }

        // Create claims matching what UserContextMiddleware expects
        // ObjectId = email address (UPN), always
        var email = person.Email ?? node.Id;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, email),
            new(ClaimTypes.Name, person.Name),
            new("name", person.Name),
            new(ClaimTypes.Email, email),
            new("email", email),
        };

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
