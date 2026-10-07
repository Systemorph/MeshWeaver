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
public sealed class RegistryRefusedException(string message) : InvalidOperationException(message);
