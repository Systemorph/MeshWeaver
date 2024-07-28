using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;

namespace OpenSmc.Layout;

public record UiActionContext(string Area, object Payload, IMessageHub Hub, LayoutAreaHost Host);
