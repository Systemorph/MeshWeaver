namespace MeshWeaver.Charting.Models;

public interface IDataSetWithFill
{
    /// <summary>
    ///  Mode                      Type       Values
    /// ==============================================================
    ///  Absolute dataset index    number     1, 2, 3, ...
    ///  Relative dataset index    string     '-1', '-2', '+1', ...
    ///  Boundary                  string     'start', 'end', 'origin'
    ///  Disabled                  boolean    false
    ///  Stacked value below       string     'stack'
    ///  Axis value                object     { value: number; }
    ///  Shape(fill inside line)   string     'shape'
    /// </summary>
    object Fill { get; init; }
}