using System.Reflection;
using OpenSmc.Messaging;
using OpenSmc.Scopes;

namespace OpenSmc.Layout.Composition;

public record AreaNode(UiControl Control, IMessageHub Hub, string Area, ViewDefinition ViewDefinition, IReadOnlyCollection<(IMutableScope Scope, PropertyInfo Property)> Dependencies, SetAreaOptions Options);

