namespace Monitor.Core.HyperV;

using Monitor.Core.Models;

internal static class VmStateMapper
{
    // Maps the CIM EnabledState value (Msvm_SummaryInformation / Msvm_ComputerSystem) to VmState.
    public static VmState FromEnabledState(ushort enabledState) => enabledState switch
    {
        2 => VmState.Running,
        3 => VmState.Off,
        32768 => VmState.Paused,
        32769 => VmState.Saved,
        32770 => VmState.Starting,
        32773 => VmState.Saving,
        32774 => VmState.Stopping,
        32776 => VmState.Pausing,
        32777 => VmState.Resuming,
        _ => VmState.Other
    };
}
