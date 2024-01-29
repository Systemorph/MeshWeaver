namespace OpenSmc.DataSetReader.Abstractions
{
    public class MappingOrderAttribute : Attribute
    {
        public MappingOrderAttribute(int order)
        {
            Order = order;
        }

        public int Length { get; set; }
        public int Order { get; set; }
    }
}