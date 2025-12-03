using System.Security.Claims;

namespace MeshWeaver.Todo.Domain;

/// <summary>
/// Manages responsible persons for todo items
/// </summary>
public static class ResponsiblePersons
{
    /// <summary>
    /// List of available responsible persons with gender-neutral names
    /// </summary>
    public static readonly string[] AvailablePersons = [
        "Alex Morgan",
        "Jordan Smith", 
        "Casey Johnson",
        "Riley Chen",
        "Avery Taylor",
        "Quinn Rodriguez",
        "Sage Williams",
        "Cameron Davis"
    ];

    /// <summary>
    /// Gets the current user's name from the claims principal, or returns a default user
    /// </summary>
    /// <param name="user">The claims principal representing the current user</param>
    /// <returns>The current user's name or a default sample user</returns>
    public static string GetCurrentUser(ClaimsPrincipal? user = null)
    {
        // Try to get the user's name from claims
        if (user?.Identity?.IsAuthenticated == true)
        {
            var name = user.FindFirst(ClaimTypes.Name)?.Value
                      ?? user.FindFirst("preferred_username")?.Value
                      ?? user.FindFirst("name")?.Value;

            if (!string.IsNullOrEmpty(name))
                return name;
        }

        // Default to the first sample user when not authenticated
        return AvailablePersons[0]; // "Alex Morgan"
    }

    /// <summary>
    /// Gets a random responsible person for sample data generation
    /// </summary>
    /// <param name="random">Random number generator</param>
    /// <returns>A randomly selected responsible person</returns>
    public static string GetRandomPerson(Random random)
    {
        return AvailablePersons[random.Next(AvailablePersons.Length)];
    }

    /// <summary>
    /// Checks if a person name is the current user
    /// </summary>
    /// <param name="personName">The person name to check</param>
    /// <param name="user">The current user claims principal</param>
    /// <returns>True if the person is the current user</returns>
    public static bool IsCurrentUser(string personName, ClaimsPrincipal? user = null)
    {
        return string.Equals(personName, GetCurrentUser(user), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets a colored indicator for the current user
    /// </summary>
    /// <param name="personName">The person name to check</param>
    /// <param name="user">The current user claims principal</param>
    /// <returns>Colored indicator if current user, empty string otherwise</returns>
    public static string GetCurrentUserIndicator(string personName, ClaimsPrincipal? user = null)
    {
        return IsCurrentUser(personName, user) ? "🟢" : "";
    }
    
    /// <summary>
    /// Gets a formatted display name with current user indicator
    /// </summary>
    /// <param name="personName">The person name</param>
    /// <param name="user">The current user claims principal</param>
    /// <returns>Formatted display name with indicator</returns>
    public static string GetDisplayName(string personName, ClaimsPrincipal? user = null)
    {
        return IsCurrentUser(personName, user) ? $"🟢 {personName} (You)" : personName;
    }
}