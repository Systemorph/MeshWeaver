namespace MeshWeaver.Connection.Notebook
{
    public class MeshWeaverKernelException : Exception
    {
        public MeshWeaverKernelException() { }

        public MeshWeaverKernelException(string message) : base(message) { }

        public MeshWeaverKernelException(string message, Exception innerException) : base(message, innerException) { }
    }
}
