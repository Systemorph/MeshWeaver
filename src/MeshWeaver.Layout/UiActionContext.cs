using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout;

public record UiActionContext(string Area, object Payload, IMessageHub Hub, LayoutAreaHost Host);
