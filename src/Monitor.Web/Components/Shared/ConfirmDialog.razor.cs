namespace Monitor.Web.Components.Shared;

using Microsoft.AspNetCore.Components;

using MudBlazor;

public partial class ConfirmDialog
{
    [CascadingParameter]
    private IMudDialogInstance MudDialog { get; set; } = default!;

    [Parameter]
    public string Title { get; set; } = "確認";

    [Parameter]
    public string Message { get; set; } = "実行しますか？";

    [Parameter]
    public string ConfirmText { get; set; } = "実行";

    [Parameter]
    public Color ConfirmColor { get; set; } = Color.Primary;

    private void Submit() => MudDialog.Close(DialogResult.Ok(true));

    private void Cancel() => MudDialog.Cancel();
}
