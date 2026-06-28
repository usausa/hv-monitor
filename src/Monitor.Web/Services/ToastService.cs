namespace Monitor.Web.Services;

using MudBlazor;

// Wrapper around MudBlazor ISnackbar that provides Success / Info / Warning / Error toasts.
public sealed class ToastService
{
    private readonly ISnackbar snackbar;

    public ToastService(ISnackbar snackbar)
    {
        this.snackbar = snackbar;
    }

    public void Success(string message) => snackbar.Add(message, Severity.Success);

    public void Info(string message) => snackbar.Add(message, Severity.Info);

    public void Warning(string message) => snackbar.Add(message, Severity.Warning);

    public void Error(string message) => snackbar.Add(message, Severity.Error);
}
