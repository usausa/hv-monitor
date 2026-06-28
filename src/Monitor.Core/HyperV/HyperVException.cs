namespace Monitor.Core.HyperV;

public sealed class HyperVException : Exception
{
    public HyperVException(string message)
        : base(message)
    {
    }

    public HyperVException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
