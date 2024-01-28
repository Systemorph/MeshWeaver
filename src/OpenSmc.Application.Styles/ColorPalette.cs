namespace OpenSmc.Application.Styles
{
    public class ColorPalette
    {
        private readonly string[] colors;

        public ColorPalette(IReadOnlyCollection<string> colors)
            :this(colors.ToArray())
        {
        }

        public ColorPalette(params string[] colors)
        {
            this.colors = colors;
        }

        public string this[int index]
        {
            get => colors[index];
            set => colors[index] = value;
        }
    }
}