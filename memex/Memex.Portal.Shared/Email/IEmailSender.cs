namespace Memex.Portal.Shared.Email;

/// <summary>
/// Sends outbound system email. Not hub-reachable — called from Blazor click actions and the
/// invitation flow — so it exposes a reactive shape (<see cref="IObservable{T}"/>) that composes
/// cleanly with the rest of the codebase's reactive pipelines. The implementation bridges its
/// async leaf (Microsoft Graph) via <c>Observable.FromAsync</c>.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends an HTML email. The returned observable is cold — the send runs on Subscribe and
    /// emits a single <c>true</c> on success, or surfaces the failure via OnError.
    /// </summary>
    IObservable<bool> SendEmail(string toAddress, string subject, string htmlBody);
}
