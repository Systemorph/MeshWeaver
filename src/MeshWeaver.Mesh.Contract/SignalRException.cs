namespace MeshWeaver.Mesh.Contract
{
    public class SignalRException : Exception
    {
        public SignalRException() { }

        public SignalRException(string message) : base(message) { }

        public SignalRException(string message, Exception innerException) : base(message, innerException) { }
    }
}
