namespace Monitor.Core.HyperV;

// Thrown when the requested virtual machine does not exist on this host.
// Callers map this to HTTP 404, unlike other HyperVException failures.
public sealed class VmNotFoundException : HyperVException
{
    public VmNotFoundException(string message)
        : base(message)
    {
    }

    public VmNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
