namespace Monitor.Web.Components.Pages;

using System.Globalization;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

using Monitor.Core.HyperV;
using Monitor.Core.Models;
using Monitor.Web.Components.Shared;
using Monitor.Web.Services;

using MudBlazor;

public partial class VmListPage
{
    private readonly string hostName = Environment.MachineName;

    private IReadOnlyList<VmInfo> virtualMachines = [];
    private bool isLoading;
    private bool isRunning;
    private bool isElevated;
    private string? errorMessage;
    private string? progressMessage;

    [Inject]
    private IHyperVService HyperVService { get; set; } = default!;

    [Inject]
    private ToastService Toast { get; set; } = default!;

    [Inject]
    private IDialogService DialogService { get; set; } = default!;

    [Inject]
    private ILogger<VmListPage> Logger { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        isElevated = HyperVService.IsElevated;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        isLoading = true;
        errorMessage = null;
        try
        {
            virtualMachines = await HyperVService.GetVirtualMachinesAsync();
        }
        catch (HyperVException ex)
        {
            errorMessage = ex.Message;
            virtualMachines = [];
        }
        finally
        {
            isLoading = false;
        }
    }

    private async Task StartAsync(VmInfo vm)
    {
        if (!await ConfirmAsync("開始", $"仮想マシン「{vm.Name}」を開始しますか？", "開始", Color.Success))
        {
            return;
        }

        await RunOperationAsync(token => HyperVService.StartAsync(vm.Id, token).AsTask(), $"開始しました: {vm.Name}");
    }

    private async Task ResumeAsync(VmInfo vm)
    {
        if (!await ConfirmAsync("再開", $"仮想マシン「{vm.Name}」を再開しますか？", "再開", Color.Success))
        {
            return;
        }

        await RunOperationAsync(token => HyperVService.ResumeAsync(vm.Id, token).AsTask(), $"再開しました: {vm.Name}");
    }

    private async Task PauseAsync(VmInfo vm)
    {
        if (!await ConfirmAsync("一時停止", $"仮想マシン「{vm.Name}」を一時停止しますか？", "一時停止", Color.Primary))
        {
            return;
        }

        await RunOperationAsync(token => HyperVService.PauseAsync(vm.Id, token).AsTask(), $"一時停止しました: {vm.Name}");
    }

    private async Task SaveAsync(VmInfo vm)
    {
        if (!await ConfirmAsync("状態保存", $"仮想マシン「{vm.Name}」の状態を保存して停止しますか？", "状態保存", Color.Info))
        {
            return;
        }

        await RunOperationAsync(token => HyperVService.SaveAsync(vm.Id, token).AsTask(), $"状態を保存しました: {vm.Name}");
    }

    private async Task ShutdownAsync(VmInfo vm)
    {
        if (!await ConfirmAsync("シャットダウン", $"仮想マシン「{vm.Name}」をシャットダウンしますか？（ゲスト OS に正常終了を要求します）", "シャットダウン", Color.Warning))
        {
            return;
        }

        await RunOperationAsync(token => HyperVService.ShutdownAsync(vm.Id, force: false, token).AsTask(), $"シャットダウンを要求しました: {vm.Name}");
    }

    private async Task TurnOffAsync(VmInfo vm)
    {
        if (!await ConfirmAsync("強制停止", $"仮想マシン「{vm.Name}」を強制的に電源オフします。ゲスト OS 内の未保存データは失われる可能性があります。よろしいですか？", "強制停止", Color.Error))
        {
            return;
        }

        await RunOperationAsync(token => HyperVService.TurnOffAsync(vm.Id, token).AsTask(), $"強制停止しました: {vm.Name}");
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText, Color confirmColor)
    {
        var parameters = new DialogParameters<ConfirmDialog>
        {
            { x => x.Title, title },
            { x => x.Message, message },
            { x => x.ConfirmText, confirmText },
            { x => x.ConfirmColor, confirmColor }
        };

        var dialog = await DialogService.ShowAsync<ConfirmDialog>(title, parameters);
        var result = await dialog.Result;
        return result is { Canceled: false };
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation, string successMessage)
    {
        isRunning = true;
        progressMessage = "処理中...";
        errorMessage = null;
        try
        {
            await operation(CancellationToken.None);
            Logger.LogInformation("VM operation succeeded: {Message}", successMessage);
            Toast.Success(successMessage);
            await LoadAsync();
        }
        catch (HyperVException ex)
        {
            Logger.LogError(ex, "VM operation failed: {Message}", ex.Message);
            errorMessage = ex.Message;
            Toast.Error(ex.Message);
        }
        finally
        {
            isRunning = false;
            progressMessage = null;
        }
    }

    private static Color StateColor(VmState state) => state switch
    {
        VmState.Running => Color.Success,
        VmState.Off => Color.Default,
        VmState.Paused => Color.Warning,
        VmState.Pausing => Color.Warning,
        VmState.Saved => Color.Info,
        VmState.Saving => Color.Info,
        VmState.Starting => Color.Info,
        VmState.Stopping => Color.Info,
        VmState.Resuming => Color.Info,
        _ => Color.Secondary
    };

    private static string StateLabel(VmState state) => state switch
    {
        VmState.Running => "実行中",
        VmState.Off => "停止",
        VmState.Paused => "一時停止",
        VmState.Saved => "保存済み",
        VmState.Starting => "起動中",
        VmState.Stopping => "停止中",
        VmState.Saving => "保存中",
        VmState.Pausing => "一時停止中",
        VmState.Resuming => "再開中",
        _ => "不明"
    };

    private static string FormatCpu(int? cpu) =>
        cpu?.ToString(CultureInfo.CurrentCulture) ?? "-";

    private static string FormatMemory(long? memoryMb) =>
        memoryMb?.ToString("N0", CultureInfo.CurrentCulture) ?? "-";

    private static string FormatUptime(TimeSpan? uptime)
    {
        if (uptime is null)
        {
            return "-";
        }

        var value = uptime.Value;
        return value.Days > 0
            ? $"{value.Days}d {value.Hours:D2}:{value.Minutes:D2}:{value.Seconds:D2}"
            : $"{value.Hours:D2}:{value.Minutes:D2}:{value.Seconds:D2}";
    }
}
