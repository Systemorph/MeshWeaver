// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Portal.Shared.Web.Layout;

internal record MobileNavMenuEntry(string Text, Func<Task> OnClick, Icon? Icon = null, Regex? LinkMatchRegex = null);
