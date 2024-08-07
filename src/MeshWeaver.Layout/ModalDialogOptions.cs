namespace MeshWeaver.Layout;

// TODO V10: Is this used anywhere? (23.03.2024, Roland Bürgi)
public record ModalDialogOptions(string Size, bool IsClosable)
{
    public ModalDialogOptions()
        :this(Sizes.Medium, false)
    {
    }

    public static class Sizes
    {
        public const string Medium = "M";
        public const string Large = "L";
        public const string Small = "S";
    }
}