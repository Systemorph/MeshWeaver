namespace MeshWeaver.Mesh
{
    public class MeshException : Exception
    {
        public MeshException() { }

        public MeshException(string message) : base(message) { }

        public MeshException(string message, Exception innerException) : base(message, innerException) { }
    }
}
