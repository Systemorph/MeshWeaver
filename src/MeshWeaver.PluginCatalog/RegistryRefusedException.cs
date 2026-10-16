using System;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// A registry's DECIDED refusal: an answer the registry will give identically next time (no such
/// manifest, blob or repository; a challenge or token exchange that is not an OCI registry's; a
/// manifest with no bundle layer; a bundle whose content is corrupt). <see cref="TransientRegistryFailure"/>
/// classifies exactly this type as final — never a bare <see cref="InvalidOperationException"/>,
/// which any code the fetch pipeline runs (a LINQ <c>First()</c> over unexpected input) can throw
/// and which therefore reads as a crash, retried (MeshWeaver#6172 review).
/// <para>Derives from <see cref="InvalidOperationException"/> so callers that catch that type for
/// reporting keep working.</para>
/// </summary>
public class RegistryRefusedException(string message) : InvalidOperationException(message);

/// <summary>
/// The one refusal that is an answer about the CONTENT rather than about the caller: the registry
/// holds no manifest under the reference asked for (<c>404 MANIFEST_UNKNOWN</c>). For a
/// publication tag that means "nothing is sealed there" — an established absence — while every
/// other <see cref="RegistryRefusedException"/> (a refused key, a challenge that is not a
/// registry's) means the reader could not look. A reader that decides on presence must tell the
/// two apart, and catches this type for the first. Still a <see cref="RegistryRefusedException"/>,
/// so every caller that classified the 404 as final keeps doing so.
/// </summary>
public sealed class RegistryManifestUnknownException(string message) : RegistryRefusedException(message);
