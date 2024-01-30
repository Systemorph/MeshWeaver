namespace OpenSmc.Partition;

public class PartitionException : Exception
{
    public PartitionException() : base("Some errors occurred during execution. Please see Log for details.") { }
    public PartitionException(string message) : base(message) { }
    public PartitionException(string message, Exception inner) : base(message, inner) { }
}

public class AggregatedPartitionException : Exception
{
    public AggregatedPartitionException(IEnumerable<string> errors)
    {
        Errors = errors;
    }
    public IEnumerable<string> Errors { get;}

    public override string Message => $"Some errors occurred during execution.Please see Log for details. \r\n {string.Join("\r\n", Errors)}";
}
