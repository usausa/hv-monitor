namespace Monitor.Web.Services;

using Monitor.Core.Models;

// Determines which operations are available for a given VM state.
public static class VmActionPolicy
{
    public static bool CanStart(VmState state) => state is VmState.Off or VmState.Saved;

    public static bool CanResume(VmState state) => state is VmState.Paused;

    public static bool CanShutdown(VmState state) => state is VmState.Running;

    public static bool CanTurnOff(VmState state) => state is VmState.Running or VmState.Paused;

    public static bool CanPause(VmState state) => state is VmState.Running;

    public static bool CanSave(VmState state) => state is VmState.Running;
}
