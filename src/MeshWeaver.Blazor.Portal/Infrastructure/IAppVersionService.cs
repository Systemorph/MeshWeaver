// ------------------------------------------------------------------------
// MIT License - Copyright (c) Microsoft Corporation. All rights reserved.
// ------------------------------------------------------------------------

namespace MeshWeaver.Blazor.Portal.Infrastructure;

/// <summary>
/// Provides the application's current version string for cache-busting and display.
/// </summary>
public interface IAppVersionService
{
    /// <summary>Gets the current application version string.</summary>
    string Version { get; }
}
