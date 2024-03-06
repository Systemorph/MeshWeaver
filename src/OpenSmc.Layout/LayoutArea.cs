using System.ComponentModel.DataAnnotations;

namespace OpenSmc.Layout;

public record LayoutArea([property: Key]string Area, UiControl Control);

