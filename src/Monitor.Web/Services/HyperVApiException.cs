namespace Monitor.Web.Services;

// Raised when an Agent operation returns a non-success HTTP response.
public sealed class HyperVApiException : Exception
{
    public HyperVApiException()
    {
    }

    public HyperVApiException(string message)
        : base(message)
    {
    }

    public HyperVApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
