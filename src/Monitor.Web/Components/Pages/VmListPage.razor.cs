namespace Monitor.Web.Components.Pages;

using System.Globalization;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

using Monitor.Contracts;
using Monitor.Web.Components.Shared;
using Monitor.Web.Services;

using MudBlazor;

public partial class VmListPage
{
    private IReadOnlyList<HostVmResult> hostResults = [];
    private bool isLoading;
    private bool isRunning;
    private string? errorMessage;
    private string? progressMessage;

    [Inject]
    private IHyperVApiClient ApiClient { get; set; } = default!;

    [Inject]
    private ToastService Toast { get; set; } = default!;

    [Inject]
    private IDialogService DialogService { get; set; } = default!;

    [Inject]
    private ILogger<VmListPage> Logger { get; set; } = default!;

    // Selected host filter. An empty string means "all hosts".
    private string HostFilter { get; set; } = string.Empty;

    // Hosts that returned an error (unreachable or failed request).
    private IEnumerable<HostVmResult> FailedHosts => hostResults.Where(r => r.Error is not null);

    // Whether more than one host is configured (controls filter visibility).
    private bool HasMultipleHosts => hostResults.Count > 1;

    // Flattened, host-filtered rows shown in the table.
    private IReadOnlyList<VmRow> Rows =>
        hostResults
            .Where(r => r.Error is null)
            .Where(r => HostFilter.Length == 0 || string.Equals(r.HostName, HostFilter, StringComparison.Ordinal))
            .SelectMany(r => r.Vms.Select(vm => new VmRow(r.HostName, vm)))
            .ToArray();

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        isLoading = true;
        errorMessage = null;
        try
        {
            hostResults = await ApiClient.GetVirtualMachinesAsync();
        }
        catch (HyperVApiException ex)
        {
            errorMessage = ex.Message;
            hostResults = [];
        }
        finally
        {
            isLoading = false;
        }
    }

    private async Task StartAsync(VmRow row)
    {
        if (!await ConfirmAsync("開始", $"仮想マシン「{row.Vm.Name}」を開始しますか？", "開始", Color.Success))
        {
            return;
        }

        await RunOperationAsync(token => ApiClient.StartAsync(row.HostName, row.Vm.Id, token), $"開始しました: {row.Vm.Name}");
    }

    private async Task ResumeAsync(VmRow row)
    {
        if (!await ConfirmAsync("再開", $"仮想マシン「{row.Vm.Name}」を再開しますか？", "再開", Color.Success))
        {
            return;
        }

        await RunOperationAsync(token => ApiClient.ResumeAsync(row.HostName, row.Vm.Id, token), $"再開しました: {row.Vm.Name}");
    }

    private async Task PauseAsync(VmRow row)
    {
        if (!await ConfirmAsync("一時停止", $"仮想マシン「{row.Vm.Name}」を一時停止しますか？", "一時停止", Color.Primary))
        {
            return;
        }

        await RunOperationAsync(token => ApiClient.PauseAsync(row.HostName, row.Vm.Id, token), $"一時停止しました: {row.Vm.Name}");
    }

    private async Task SaveAsync(VmRow row)
    {
        if (!await ConfirmAsync("状態保存", $"仮想マシン「{row.Vm.Name}」の状態を保存して停止しますか？", "状態保存", Color.Info))
        {
            return;
        }

        await RunOperationAsync(token => ApiClient.SaveAsync(row.HostName, row.Vm.Id, token), $"状態を保存しました: {row.Vm.Name}");
    }

    private async Task ShutdownAsync(VmRow row)
    {
        if (!await ConfirmAsync("シャットダウン", $"仮想マシン「{row.Vm.Name}」をシャットダウンしますか？（ゲスト OS に正常終了を要求します）", "シャットダウン", Color.Warning))
        {
            return;
        }

        await RunOperationAsync(token => ApiClient.ShutdownAsync(row.HostName, row.Vm.Id, force: false, token), $"シャットダウンを要求しました: {row.Vm.Name}");
    }

    private async Task TurnOffAsync(VmRow row)
    {
        if (!await ConfirmAsync("強制停止", $"仮想マシン「{row.Vm.Name}」を強制的に電源オフします。ゲスト OS 内の未保存データは失われる可能性があります。よろしいですか？", "強制停止", Color.Error))
        {
            return;
        }

        await RunOperationAsync(token => ApiClient.TurnOffAsync(row.HostName, row.Vm.Id, token), $"強制停止しました: {row.Vm.Name}");
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
        catch (HyperVApiException ex)
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

    private sealed record VmRow(string HostName, VmInfo Vm);
}
