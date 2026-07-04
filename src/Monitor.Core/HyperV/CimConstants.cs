namespace Monitor.Core.HyperV;

internal static class CimConstants
{
    public const string VirtualizationNamespace = @"root\virtualization\v2";

    public const string SummaryInformationClass = "Msvm_SummaryInformation";

    public const string ComputerSystemClass = "Msvm_ComputerSystem";

    public const string ShutdownComponentClass = "Msvm_ShutdownComponent";

    public const string SystemDeviceAssociation = "Msvm_SystemDevice";

    public const string Cimv2Namespace = @"root\cimv2";

    public const string OperatingSystemClass = "Win32_OperatingSystem";

    public const string LogicalDiskClass = "Win32_LogicalDisk";

    public const string ProcessorClass = "Win32_Processor";

    public const string ProcessorPerfClass = "Win32_PerfFormattedData_PerfOS_Processor";

    public const string HyperVLogicalProcessorPerfClass = "Win32_PerfFormattedData_Counters_HyperVHypervisorLogicalProcessor";
}
