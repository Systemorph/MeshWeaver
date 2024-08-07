namespace MeshWeaver.Collections
{
    public static class EmptyArrays
    {
        private static readonly CreatableObjectStore<Type, int, Array> Defaults =
            new CreatableObjectStore<Type, int, Array>(GetDefaultArrayImpl);

        private static Array GetDefaultArrayImpl(Type elementType, int rank)
        {
            if (elementType == null)
                throw new ArgumentNullException(nameof(elementType));
            if (rank <= 0)
                throw new ArgumentException("Rank must be greater than zero", nameof(rank));

            var ranks = new int[rank];
            return Array.CreateInstance(elementType, ranks);
        }

        public static Array GetInstance(Type arrayType)
        {
            if (arrayType == null)
                throw new ArgumentNullException(nameof(arrayType));
            if (!arrayType.IsArray)
                throw new ArgumentException("Array type expected");

            var elementType = arrayType.GetElementType();
            var rank = arrayType.GetArrayRank();
            return Defaults.GetInstance(elementType, rank);
        }

        public static Array GetInstance(Type elementType, int rank)
        {
            return Defaults.GetInstance(elementType, rank);
        }
    }
}