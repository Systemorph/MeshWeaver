namespace MeshWeaver.Messaging;

/// <summary>
/// Opt-in on a message: a short description a diagnostic line may print instead of the message
/// itself. A line that has to say WHICH request it is about (the bring-up discard Error,
/// <c>MessageService.Dispose</c>, #5718) prints this and nothing else of the payload — never the
/// message's own <c>ToString()</c>.
///
/// <para>🚨 Why opt-in and never <c>ToString()</c>. A record's generated rendering prints EVERY
/// member, so rendering an arbitrary message would put whatever it carries into an Error line that
/// ships to the log store — a token-validation request would log its raw token in full. A type that
/// implements this interface states, once, what is safe and useful to print; a type that does not
/// is named by its type alone.</para>
///
/// <para>Implement it EXPLICITLY (<c>string IDescribedForDiagnostics.DiagnosticDescription =&gt; …</c>):
/// System.Text.Json does not serialise an explicit interface implementation, so opting in never
/// changes the message's wire shape. Keep the value cheap and free of credentials and personal data.</para>
/// </summary>
public interface IDescribedForDiagnostics
{
    /// <summary>
    /// What this message is about, safe to print in a log line — e.g. a read's reference, which
    /// names the producer that issued it. Never a credential, never a payload body.
    /// </summary>
    string DiagnosticDescription { get; }
}
