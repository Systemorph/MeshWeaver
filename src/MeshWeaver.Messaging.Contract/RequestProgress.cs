namespace MeshWeaver.Messaging;

/// <summary>
/// "I am still working on your request" — a sign of life a handler posts to the hub that is
/// awaiting a request's reply, correlated to the request exactly as the reply will be
/// (<c>PostOptions.ResponseFor</c>). It is NOT the reply: the awaiting hub keeps its callback open
/// and restarts the request's deadline, so <c>RequestTimeout</c> measures SILENCE rather than
/// total duration.
///
/// <para><b>Why it exists.</b> An operation whose size is the caller's data — a recursive delete
/// over a subtree of any size — cannot promise to finish inside a fixed ceiling, and the server
/// side already knows it: the delete's commit bound is a no-progress watchdog (#3392). With the
/// caller's deadline still a total-duration cap, the reply of every operation larger than the cap
/// reached nobody, although the operation succeeded (the 2026-10-09 delete of the retired
/// <c>Crm/Client</c> NodeType: hundreds of compile-activity satellites, the caller gave up at 60 s,
/// the delete finished seconds later). Reporting progress makes the two bounds agree: a caller still
/// gives up on 60 s of silence, never on 60 s of work.</para>
///
/// <para><b>Opt-in on both ends.</b> A hub stamps <c>PostOptions.AcceptsProgress</c> on the
/// requests it awaits, and a handler reports progress only to a request carrying that stamp — so a
/// caller that predates this type (a replica on an older image during a roll) is never sent one it
/// would mistake for its reply.</para>
/// </summary>
/// <param name="Stage">What the handler last did — diagnostic only, recorded on the request's
/// trail. Never parsed.</param>
[SystemMessage]
[CanBeIgnored]
public record RequestProgress(string? Stage = null);
