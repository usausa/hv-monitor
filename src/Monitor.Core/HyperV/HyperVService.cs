namespace Monitor.Core.HyperV;

using System.Globalization;

using Microsoft.Management.Infrastructure;

using Monitor.Contracts;
using Monitor.Core.Security;

public sealed class HyperVService : IHyperVService
{
    private const ushort EnabledStateRunning = 2;

    private const ushort RequestedStateEnabled = 2;
    private const ushort RequestedStateDisabled = 3;
    private const ushort RequestedStatePaused = 32768;
    private const ushort RequestedStateSuspended = 32769;

    private const uint ReturnValueCompleted = 0;
    private const uint ReturnValueJobStarted = 4096;

    private const ushort JobStateCompleted = 7;

    private const int OperationTimeoutMs = 5 * 60 * 1000;
    private const int JobPollIntervalMs = 200;

    public bool IsElevated => ElevationChecker.IsElevated();

    public ValueTask<IReadOnlyList<VmInfo>> GetVirtualMachinesAsync(CancellationToken cancellationToken = default) =>
        new(Task.Run<IReadOnlyList<VmInfo>>(() => QueryVirtualMachines(cancellationToken), cancellationToken));

    public ValueTask<HostMetrics> GetHostMetricsAsync(CancellationToken cancellationToken = default) =>
        new(Task.Run(() => QueryHostMetrics(cancellationToken), cancellationToken));

    public ValueTask StartAsync(string id, CancellationToken cancellationToken = default) =>
        RunStateChangeAsync(id, RequestedStateEnabled, cancellationToken);

    public ValueTask ResumeAsync(string id, CancellationToken cancellationToken = default) =>
        RunStateChangeAsync(id, RequestedStateEnabled, cancellationToken);

    public ValueTask TurnOffAsync(string id, CancellationToken cancellationToken = default) =>
        RunStateChangeAsync(id, RequestedStateDisabled, cancellationToken);

    public ValueTask PauseAsync(string id, CancellationToken cancellationToken = default) =>
        RunStateChangeAsync(id, RequestedStatePaused, cancellationToken);

    public ValueTask SaveAsync(string id, CancellationToken cancellationToken = default) =>
        RunStateChangeAsync(id, RequestedStateSuspended, cancellationToken);

    public ValueTask ShutdownAsync(string id, bool force, CancellationToken cancellationToken = default) =>
        new(Task.Run(() => ShutdownGuest(id, force, cancellationToken), cancellationToken));

    private static ValueTask RunStateChangeAsync(string id, ushort requestedState, CancellationToken cancellationToken) =>
        new(Task.Run(() => RequestStateChange(id, requestedState, cancellationToken), cancellationToken));

    // --- Query ---

    private static List<VmInfo> QueryVirtualMachines(CancellationToken cancellationToken)
    {
        const string query = $"SELECT Name, ElementName, EnabledState, ProcessorLoad, MemoryUsage, UpTime, Version FROM {CimConstants.SummaryInformationClass}";

        try
        {
            using var session = CimSession.Create(null);

            var result = new List<VmInfo>();
            foreach (var instance in session.QueryInstances(CimConstants.VirtualizationNamespace, "WQL", query))
            {
                using (instance)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var id = GetString(instance, "Name");
                    if (id is null)
                    {
                        continue;
                    }

                    var name = GetString(instance, "ElementName") ?? id;
                    var version = GetString(instance, "Version");
                    var enabledState = GetValue<ushort>(instance, "EnabledState");
                    var state = VmStateMapper.FromEnabledState(enabledState);

                    int? cpu = null;
                    long? memoryMb = null;
                    TimeSpan? uptime = null;

                    // Resource metrics are only meaningful while the VM is running.
                    if (enabledState == EnabledStateRunning)
                    {
                        cpu = GetNullableValue<ushort>(instance, "ProcessorLoad");

                        var memory = GetNullableValue<ulong>(instance, "MemoryUsage");
                        if (memory.HasValue)
                        {
                            memoryMb = (long)memory.Value;
                        }

                        var uptimeMs = GetNullableValue<ulong>(instance, "UpTime");
                        if (uptimeMs.HasValue)
                        {
                            uptime = TimeSpan.FromMilliseconds(uptimeMs.Value);
                        }
                    }

                    result.Add(new VmInfo(id, name, state, cpu, memoryMb, uptime, version));
                }
            }

            return result;
        }
        catch (CimException ex)
        {
            throw new HyperVException($"仮想マシン一覧の取得に失敗しました: {ex.Message}", ex);
        }
    }

    // --- Host metrics ---

    private static HostMetrics QueryHostMetrics(CancellationToken cancellationToken)
    {
        try
        {
            using var session = CimSession.Create(null);

            var cpu = QueryCpuPercent(session, cancellationToken);
            var (totalMb, usedMb) = QueryMemory(session, cancellationToken);
            var disks = QueryDisks(session, cancellationToken);

            return new HostMetrics(cpu, totalMb, usedMb, disks);
        }
        catch (CimException ex)
        {
            throw new HyperVException($"ホストメトリクスの取得に失敗しました: {ex.Message}", ex);
        }
    }

    private static double QueryCpuPercent(CimSession session, CancellationToken cancellationToken)
    {
        // Prefer the Hyper-V hypervisor logical-processor counter (most accurate on a Hyper-V host).
        const string hyperVQuery = $"SELECT PercentTotalRunTime FROM {CimConstants.HyperVLogicalProcessorPerfClass} WHERE Name = '_Total'";
        var hyperV = TryQueryScalar<ulong>(session, hyperVQuery, "PercentTotalRunTime", cancellationToken);
        if (hyperV.HasValue)
        {
            return hyperV.Value;
        }

        // Fallback: OS total processor time counter.
        const string osQuery = $"SELECT PercentProcessorTime FROM {CimConstants.ProcessorPerfClass} WHERE Name = '_Total'";
        var os = TryQueryScalar<ulong>(session, osQuery, "PercentProcessorTime", cancellationToken);
        if (os.HasValue)
        {
            return os.Value;
        }

        // Last resort: Win32_Processor.LoadPercentage (first socket).
        const string loadQuery = $"SELECT LoadPercentage FROM {CimConstants.ProcessorClass}";
        var load = TryQueryScalar<ushort>(session, loadQuery, "LoadPercentage", cancellationToken);
        if (load.HasValue)
        {
            return load.Value;
        }

        return 0d;
    }

    private static (long TotalMb, long UsedMb) QueryMemory(CimSession session, CancellationToken cancellationToken)
    {
        const string query = $"SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM {CimConstants.OperatingSystemClass}";

        foreach (var instance in session.QueryInstances(CimConstants.Cimv2Namespace, "WQL", query))
        {
            using (instance)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var totalKb = GetNullableValue<ulong>(instance, "TotalVisibleMemorySize") ?? 0;
                var freeKb = GetNullableValue<ulong>(instance, "FreePhysicalMemory") ?? 0;
                var usedKb = totalKb > freeKb ? totalKb - freeKb : 0;

                return ((long)(totalKb / 1024), (long)(usedKb / 1024));
            }
        }

        return (0, 0);
    }

    private static List<DiskInfo> QueryDisks(CimSession session, CancellationToken cancellationToken)
    {
        // DriveType = 3 selects local fixed disks only.
        const string query = $"SELECT DeviceID, Size, FreeSpace FROM {CimConstants.LogicalDiskClass} WHERE DriveType = 3";

        var disks = new List<DiskInfo>();
        foreach (var instance in session.QueryInstances(CimConstants.Cimv2Namespace, "WQL", query))
        {
            using (instance)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = GetString(instance, "DeviceID");
                if (name is null)
                {
                    continue;
                }

                var size = GetNullableValue<ulong>(instance, "Size") ?? 0;
                var free = GetNullableValue<ulong>(instance, "FreeSpace") ?? 0;

                disks.Add(new DiskInfo(name, (long)size, (long)free));
            }
        }

        return disks;
    }

    private static T? TryQueryScalar<T>(CimSession session, string query, string propertyName, CancellationToken cancellationToken)
        where T : struct
    {
        try
        {
            foreach (var instance in session.QueryInstances(CimConstants.Cimv2Namespace, "WQL", query))
            {
                using (instance)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return GetNullableValue<T>(instance, propertyName);
                }
            }
        }
        catch (CimException)
        {
            // Counter class may be unavailable on this host; fall back to the next source.
        }

        return null;
    }

    // --- Operations ---

    private static void RequestStateChange(string id, ushort requestedState, CancellationToken cancellationToken)
    {
        try
        {
            using var session = CimSession.Create(null);
            using var vm = FindComputerSystem(session, id);

            cancellationToken.ThrowIfCancellationRequested();

            using var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("RequestedState", requestedState, CimType.UInt16, CimFlags.In)
            };

            using var result = session.InvokeMethod(CimConstants.VirtualizationNamespace, vm, "RequestStateChange", parameters);
            HandleStateChangeResult(session, result, cancellationToken);
        }
        catch (CimException ex)
        {
            throw new HyperVException($"状態の変更に失敗しました: {ex.Message}", ex);
        }
    }

    private static void ShutdownGuest(string id, bool force, CancellationToken cancellationToken)
    {
        try
        {
            using var session = CimSession.Create(null);
            using var vm = FindComputerSystem(session, id);

            cancellationToken.ThrowIfCancellationRequested();

            using var shutdownComponent = FindShutdownComponent(session, vm)
                ?? throw new HyperVException("シャットダウン コンポーネントが見つかりません（統合サービスが無効の可能性があります）");

            using var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("Force", force, CimType.Boolean, CimFlags.In),
                CimMethodParameter.Create("Reason", "hv-monitor", CimType.String, CimFlags.In)
            };

            using var result = session.InvokeMethod(CimConstants.VirtualizationNamespace, shutdownComponent, "InitiateShutdown", parameters);
            var returnValue = Convert.ToUInt32(result.ReturnValue.Value, CultureInfo.InvariantCulture);
            if (returnValue != ReturnValueCompleted)
            {
                throw new HyperVException($"シャットダウンに失敗しました (戻り値={returnValue})");
            }
        }
        catch (CimException ex)
        {
            throw new HyperVException($"シャットダウンに失敗しました: {ex.Message}", ex);
        }
    }

    private static CimInstance FindComputerSystem(CimSession session, string id)
    {
        var query = $"SELECT * FROM {CimConstants.ComputerSystemClass} WHERE Name = '{id}'";
        foreach (var instance in session.QueryInstances(CimConstants.VirtualizationNamespace, "WQL", query))
        {
            return instance;
        }

        throw new VmNotFoundException($"仮想マシンが見つかりません: {id}");
    }

    private static CimInstance? FindShutdownComponent(CimSession session, CimInstance vm)
    {
        foreach (var instance in session.EnumerateAssociatedInstances(
            CimConstants.VirtualizationNamespace,
            vm,
            CimConstants.SystemDeviceAssociation,
            CimConstants.ShutdownComponentClass,
            null,
            null))
        {
            return instance;
        }

        return null;
    }

    private static void HandleStateChangeResult(CimSession session, CimMethodResult result, CancellationToken cancellationToken)
    {
        var returnValue = Convert.ToUInt32(result.ReturnValue.Value, CultureInfo.InvariantCulture);
        if (returnValue == ReturnValueCompleted)
        {
            return;
        }

        if (returnValue == ReturnValueJobStarted)
        {
            if (result.OutParameters["Job"]?.Value is CimInstance jobReference)
            {
                using (jobReference)
                {
                    WaitForJob(session, jobReference, cancellationToken);
                }
            }

            return;
        }

        throw new HyperVException($"操作が失敗しました (戻り値={returnValue})");
    }

    private static void WaitForJob(CimSession session, CimInstance jobReference, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + OperationTimeoutMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var job = session.GetInstance(CimConstants.VirtualizationNamespace, jobReference);
            var jobState = GetValue<ushort>(job, "JobState");

            if (jobState == JobStateCompleted)
            {
                return;
            }

            if (jobState > JobStateCompleted)
            {
                var description = GetString(job, "ErrorDescription");
                throw new HyperVException(
                    string.IsNullOrEmpty(description)
                        ? $"ジョブが失敗しました (状態={jobState})"
                        : $"ジョブが失敗しました: {description}");
            }

            if (Environment.TickCount64 > deadline)
            {
                throw new HyperVException("操作がタイムアウトしました");
            }

            Thread.Sleep(JobPollIntervalMs);
        }
    }

    // --- Helpers ---

    private static string? GetString(CimInstance instance, string propertyName) =>
        instance.CimInstanceProperties[propertyName]?.Value as string;

    private static T GetValue<T>(CimInstance instance, string propertyName)
        where T : struct
    {
        var value = instance.CimInstanceProperties[propertyName]?.Value;
        return value is T typed ? typed : default;
    }

    private static T? GetNullableValue<T>(CimInstance instance, string propertyName)
        where T : struct
    {
        var value = instance.CimInstanceProperties[propertyName]?.Value;
        return value is T typed ? typed : null;
    }
}
