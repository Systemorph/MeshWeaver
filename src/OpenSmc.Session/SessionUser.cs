
namespace OpenSmc.Session;

public record AccessTokenResult(string AccessToken, DateTimeOffset ExpiresOn);

public interface IPrincipal
{
    Task<AccessTokenResult> GetAccessToken(string[] scopes);
}
