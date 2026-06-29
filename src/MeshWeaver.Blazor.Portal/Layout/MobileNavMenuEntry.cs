// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Portal.Layout;

/// <summary>
/// A single entry in the mobile navigation menu: a label, a click handler, and optional
/// icon and active-link matching.
/// </summary>
/// <param name="Text">The display label shown for the entry.</param>
/// <param name="OnClick">Asynchronous handler invoked when the entry is selected.</param>
/// <param name="Icon">Optional icon rendered alongside the label.</param>
/// <param name="LinkMatchRegex">Optional pattern matched against the current route to mark the entry as active.</param>
public record MobileNavMenuEntry(string Text, Func<Task> OnClick, Icon? Icon = null, Regex? LinkMatchRegex = null);
