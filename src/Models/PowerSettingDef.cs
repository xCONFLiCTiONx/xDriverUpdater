namespace xDriverUpdater.Models;

/// <summary>
/// Verified Windows 11 Power Scheme, Subgroup, and Setting GUIDs.
/// </summary>
public static class PowerGuids
{
    // Built-in base schemes
    public static readonly Guid SchemeBalanced        = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid SchemeHighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid SchemePowerSaver      = new("a1841308-3541-4fab-bc81-f71556f20b4a");

    // Subgroups
    public static readonly Guid SubNone      = new("fea3413e-7e05-4911-9a71-700331f1c294");
    public static readonly Guid SubDisk      = new("0012ee47-9041-4b5d-9b77-535fba8b1442");
    public static readonly Guid SubSleep     = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    public static readonly Guid SubUsb       = new("2a737441-1930-4402-8d77-b2bebba308a3");
    public static readonly Guid SubIdleRes   = new("2e601130-5351-4d9d-8e04-252966bad054");
    public static readonly Guid SubPci       = new("501a4d13-42af-4429-9fd1-a8218c268e20");
    public static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    public static readonly Guid SubDisplay   = new("7516b95f-f776-4464-8c53-06167f40cc99");
    public static readonly Guid SubWireless  = new("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");

    // General (SubNone)
    public static readonly Guid RequirePasswordOnWakeup = new("0e796bdb-100d-47d6-a2d5-f7d2daa51f51");

    // Display (SubDisplay)
    public static readonly Guid DisplayTurnOffAfter        = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"); // seconds
    public static readonly Guid AllowDisplayRequiredPolicy = new("a9ceb8da-cd46-44fb-a98b-02af69de4623"); // 0=Off, 1=On

    // Sleep (SubSleep)
    public static readonly Guid SleepAfter                = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"); // seconds
    public static readonly Guid HibernateAfter            = new("9d7815a6-7ee4-497e-8888-515a05f02364"); // seconds
    public static readonly Guid AllowHybridSleep          = new("94ac6d29-73ce-41a6-809f-6363ba21b47e"); // 0=Off, 1=On
    public static readonly Guid AllowSystemRequiredPolicy = new("a4b195f5-8225-47d8-8012-9d41369786e2"); // 0=Off, 1=On (xTaskForge!)
    public static readonly Guid AllowStandbyStates        = new("abfc2519-3608-4c2a-94ea-171b0ed546ab"); // 0=Off, 1=On
    public static readonly Guid AllowWakeTimers           = new("bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d"); // 0=Disable, 1=Enable, 2=Important Only
    public static readonly Guid AllowAwayModePolicy       = new("25dfa149-5dd1-4736-b5ab-e8a37b5b8187"); // 0=Off, 1=On

    // Processor (SubProcessor)
    public static readonly Guid MinProcessorState    = new("893dee8e-2bef-41e0-89c6-b55d0929964c"); // 0..100%
    public static readonly Guid MaxProcessorState    = new("bc5038f7-23e0-4960-96da-33abaf5935ec"); // 0..100%
    public static readonly Guid PerfBoostMode        = new("be337238-0d82-4146-a960-4f3749d470c7"); // 0=Disabled, 1=Enabled, 2=Aggressive, 3=Eff.Enabled, 4=Eff.Aggressive
    public static readonly Guid PerfBoostPolicy      = new("45bcc044-d885-43e2-8605-ee0ec6e96b59"); // 0..100%
    public static readonly Guid EnergyPerfPreference = new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"); // 0..100 (0=Perf, 100=Eco)
    public static readonly Guid CoreParkingMinCores  = new("0cc5b647-c1df-4637-891a-dec35c318583"); // 0..100%
    public static readonly Guid SystemCoolingPolicy  = new("94d3a615-a899-4ac5-ae2b-e4d8f634367f"); // 0=Passive, 1=Active

    // PCI Express (SubPci)
    public static readonly Guid PcieLinkStatePowerMgmt = new("ee12f906-d277-404b-b6da-e5fa1a576df5"); // 0=Off, 1=Moderate, 2=Maximum

    // Hard Disk / NVMe / SATA (SubDisk)
    public static readonly Guid DiskTurnOffAfter    = new("6738e2c4-e8a5-4a42-b16a-e040e769756e"); // seconds (0=Never)
    public static readonly Guid AhciLinkPowerMgmt   = new("0b2d69d7-a2a1-449c-9680-f91c70521c60"); // 0=Active, 1=HIPM, 2=HIPM+DIPM, 3=Lowest, 4=DIPM
    public static readonly Guid PrimaryNvmeIdleMs   = new("d639518a-e56d-4345-8af2-b9f32fb26109"); // ms
    public static readonly Guid PrimaryNvmeLatTolMs = new("fc95af4d-40e7-4b6d-835a-56d131dbc80e"); // ms
    public static readonly Guid NvmeNoppme          = new("fc7372b6-ab2d-43ee-8797-15e9841f2cca"); // 0=Off, 1=On

    // USB (SubUsb)
    public static readonly Guid UsbSelectiveSuspend = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226"); // 0=Disabled, 1=Enabled
    public static readonly Guid Usb3LinkPowerMgmt   = new("d4e98f31-5ffe-4ce1-be31-1b38b384c009"); // 0=Off, 1=Minimum, 2=Moderate, 3=Maximum

    // Wireless (SubWireless)
    public static readonly Guid WirelessPowerSaving = new("12bbebe6-58d6-4636-95bb-3217ef867c1a"); // 0=Max Performance, 1=Low, 2=Medium, 3=Max Saving
}

public sealed record PowerSettingEntry(
    string Category,
    string Name,
    Guid SubgroupGuid,
    Guid SettingGuid,
    uint TargetValue,
    string Reason,
    Func<uint, string> Formatter
);
