using System;

namespace MeshWeaver.Messaging;

/// <summary>
/// The router's designated SPOKESMAN — "no one must ever publish from main hub" (maintainer,
/// 2026-09-01). Infrastructure that would otherwise post THROUGH the router (a recycled
/// workspace announcing <c>StreamEndedEvent</c> to its subscribers is the measured case: the
/// compile+render gate logged a <c>ROUTER_TRAFFIC</c> violation per recycled activity) asks the
/// router's configuration for this instead and posts from the hub <see cref="Resolve"/> returns —
/// on a mesh, the dedicated <c>portal/nodeops-{meshId}</c> execution hub, registered by
/// <c>MeshBuilder</c>. <see cref="Resolve"/> receives the router hub and may return null while
/// the mesh is tearing down; the caller then falls back to its own recovery path.
/// </summary>
/// <param name="Resolve">Given the router hub, the non-router hub to speak through — or null when
/// none can be materialised (teardown).</param>
public sealed record RouterCarrier(Func<IMessageHub, IMessageHub?> Resolve);
