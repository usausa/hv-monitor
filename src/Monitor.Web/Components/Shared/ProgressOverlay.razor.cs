namespace Monitor.Web.Components.Shared;

using Microsoft.AspNetCore.Components;

public partial class ProgressOverlay
{
    [Parameter]
    public bool IsVisible { get; set; }

    [Parameter]
    public string? Message { get; set; }
}
