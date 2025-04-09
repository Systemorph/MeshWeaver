namespace MeshWeaver.Domain;

public class AlignAttribute : Attribute
{
    public Align Align;
}

public enum Align{ Start, Center, End}
