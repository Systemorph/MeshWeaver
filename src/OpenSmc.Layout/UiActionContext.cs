using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;

namespace OpenSmc.Layout;

public record UiActionContext(object Payload, IMessageHub Hub, LayoutAreaHost Layout);
