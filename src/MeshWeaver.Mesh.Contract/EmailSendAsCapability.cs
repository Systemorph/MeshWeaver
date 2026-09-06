namespace MeshWeaver.Mesh;

/// <summary>
/// What is known about a sender's ability to send AS a particular person. Three states, because
/// there are three states of the world — see <see cref="EmailSendAsCapability"/> for why the
/// two-answer shape it replaces was a defect (#3450).
/// </summary>
public enum EmailSendAs
{
    /// <summary>
    /// The check COMPLETED and the answer is no: this sender cannot act as that person right now
    /// (no connected mailbox, or a sender that has no delegated path at all).
    ///
    /// <para>This is the ONLY state in which telling the user "your mailbox is not connected" and
    /// offering a Connect button is truthful.</para>
    /// </summary>
    Unavailable,

    /// <summary>The check completed and the sender can send as that person.</summary>
    Available,

    /// <summary>
    /// The check produced NO answer — it timed out, the transport faulted, or the sender could not
    /// interpret what it read. <b>Nothing is known</b> about whether the person connected.
    ///
    /// <para>A UI here must say so ("we could not check just now") and must not assert either of
    /// the other two. Rendering this as <see cref="Unavailable"/> tells a connected user they have
    /// not connected — that is #3450, and #3433 one layer up.</para>
    /// </summary>
    Undetermined,
}

/// <summary>
/// The answer to "can this sender send as <c>userObjectId</c> right now?", carrying the
/// <see cref="State"/> and a <see cref="Diagnostic"/> that says WHY when the answer is not a clean
/// yes.
///
/// <para>🚨 <b>Why this type exists (#3450).</b> <see cref="IEmailSender.CanSendAsUser"/> returns
/// <c>IObservable&lt;bool&gt;</c> — two answers for three states of the world. A read that never
/// completed and a read that found no credential both arrived as <c>false</c>, and the send dialog
/// rendered <c>false</c> as a user-visible <i>"your Microsoft 365 mailbox is not connected yet"</i>
/// beside a Connect button. On a transient fault that statement is simply untrue, and a connected
/// user was told to connect. Widening the answer is the fix; widening a timeout is not.</para>
///
/// <para>Deliberately its own small type rather than <see cref="EaGraphAccess"/>: this is a
/// property of the SENDER, and a sender need not be the Executive Assistant's Graph one. It carries
/// no token and no SDK type, so it stays usable from any transport.</para>
///
/// <para><b>The rendering rule is on the type, not in each UI.</b> <see cref="OffersConnect"/> is
/// true for exactly one state, so a consumer cannot re-derive the #3450 collapse by writing
/// <c>!IsAvailable</c>.</para>
/// </summary>
/// <param name="State">What is known.</param>
/// <param name="Diagnostic">
/// Why the answer is what it is — for logs, and for a message a human can act on. Required for
/// <see cref="EmailSendAs.Undetermined"/> (an undetermined answer with nothing to say is
/// indistinguishable from the swallow this type removes); optional otherwise. Developer-facing:
/// it is a log/diagnostic string, never chrome a viewer reads, so it is not localized.
/// </param>
public sealed record EmailSendAsCapability(EmailSendAs State, string? Diagnostic = null)
{
    /// <summary>True only for <see cref="EmailSendAs.Available"/> — never for the other two.</summary>
    public bool IsAvailable => State == EmailSendAs.Available;

    /// <summary>True only for <see cref="EmailSendAs.Undetermined"/>.</summary>
    public bool IsUndetermined => State == EmailSendAs.Undetermined;

    /// <summary>
    /// 🚨 Whether a UI may state "you have not connected your mailbox" and offer a Connect button.
    ///
    /// <para>True for <see cref="EmailSendAs.Unavailable"/> ALONE. It is deliberately not
    /// <c>!IsAvailable</c>: that expression is the #3450 defect written out, because it folds
    /// <see cref="EmailSendAs.Undetermined"/> — "we could not check" — into a confident claim the
    /// viewer can see is false. An undetermined check gets its own panel that says what happened
    /// and offers a retry.</para>
    /// </summary>
    public bool OffersConnect => State == EmailSendAs.Unavailable;

    /// <summary>The check completed and the sender can act as this person.</summary>
    public static EmailSendAsCapability Available { get; } = new(EmailSendAs.Available);

    /// <summary>
    /// The check COMPLETED and the answer is no. Not the fallback for a check that failed —
    /// <see cref="Unknown(string)"/> is.
    /// </summary>
    /// <param name="diagnostic">Optional detail for logs.</param>
    public static EmailSendAsCapability Unavailable(string? diagnostic = null) =>
        new(EmailSendAs.Unavailable, diagnostic);

    /// <summary>
    /// The check produced no answer. <paramref name="diagnostic"/> is REQUIRED and must be
    /// non-blank: it is the only thing separating a modelled unknown from the silent
    /// <c>catch → false</c> this type exists to remove.
    /// </summary>
    /// <param name="diagnostic">What stopped the check from answering.</param>
    /// <exception cref="ArgumentException">The diagnostic is null, empty or whitespace.</exception>
    public static EmailSendAsCapability Unknown(string diagnostic) =>
        string.IsNullOrWhiteSpace(diagnostic)
            ? throw new ArgumentException(
                "An undetermined send-as capability must say what stopped the check from "
                + "answering; an unknown with nothing to say is the swallow this type removes.",
                nameof(diagnostic))
            : new(EmailSendAs.Undetermined, diagnostic);

    /// <summary>
    /// The undetermined answer for a check that FAULTED, naming the exception so the state is
    /// greppable rather than merely modelled.
    /// </summary>
    /// <param name="error">The fault the check surfaced.</param>
    public static EmailSendAsCapability Unknown(Exception error) =>
        Unknown($"the send-as check faulted: {error.GetType().Name}: {error.Message}");
}
