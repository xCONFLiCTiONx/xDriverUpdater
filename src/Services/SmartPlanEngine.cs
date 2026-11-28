using System.IO;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

public sealed class SmartPlanEngine
{
    private readonly PowerCfgService _powerCfg;

    public SmartPlanEngine(PowerCfgService powerCfg)
    {
        _powerCfg = powerCfg;
    }

    /// <summary>
    /// Generates a hardware-aware preset tailored specifically to the detected machine.
    /// </summary>
    public PowerPlanConfig CreatePreset(PresetKind kind, PowerHardwareProfile hw)
    {
        var cfg = new PowerPlanConfig
        {
            Preset = kind,
            AllowHybridSleep = hw.SupportsHybridSleep && !hw.HypervisorBlocksHybridSleep,
            RespectMediaKeepAwake = true,
            HibernateAfterMinutes = hw.HasBattery ? 60 : 0, // Desktop SSDs shouldn't burn TBW hibernating
            DiskTurnOffMinutes = 0, // Never spin-down / park SATA/NVMe/USB drives aggressively
            UsbSelectiveSuspend = !hw.HasExternalUsbDrive && hw.HasBattery,
            WirelessPowerSaving = 0
        };

        switch (kind)
        {
            case PresetKind.SmartDevAi:
                cfg.PlanName = "xPowerPlan — Smart Dev & AI";
                cfg.PlanDescription = $"Tuned for {hw.CpuName} + {(hw.Gpus.FirstOrDefault()?.Name ?? "GPU")}. Fast IDE/AI bursts, clean S3 sleep, xTaskForge media-friendly.";
                cfg.DisplayOffMinutes = 10;
                cfg.SleepAfterMinutes = 15;
                cfg.WakeTimersMode = 2; // Important Wake Timers Only
                cfg.MinProcessorState = 5; // Allows CPPC2 idle downclocking
                cfg.MaxProcessorState = 100;
                cfg.BoostMode = 4; // Efficient Aggressive (ideal for Ryzen CPPC2)
                cfg.BoostPolicy = 100;
                cfg.EnergyPerfPreference = 20; // Snappy response without pinning voltage at idle
                cfg.CoreParkingMinCores = 100; // Keep cores unparked for bursty dev/AI workloads
                cfg.CoolingPolicy = 1; // Active
                cfg.PcieLinkStatePowerMgmt = hw.HasNvidiaGpu || hw.HasNvmeSsd ? 0 : 1; // Off for zero RTX/NVMe wake latency
                cfg.AhciLinkPowerMgmt = 0; // Active (zero SATA SSD wake stutter)
                cfg.PrimaryNvmeIdleMs = 200;
                cfg.PrimaryNvmeLatTolMs = 15;
                cfg.Usb3LinkPowerMgmt = hw.HasExternalUsbDrive ? 0 : 1;
                break;

            case PresetKind.MaxPerformance:
                cfg.PlanName = "xPowerPlan — Max Performance";
                cfg.PlanDescription = $"Maximum responsiveness for heavy builds & gaming on {hw.CpuName} + {(hw.Gpus.FirstOrDefault()?.Name ?? "GPU")}.";
                cfg.DisplayOffMinutes = 15;
                cfg.SleepAfterMinutes = 30;
                cfg.WakeTimersMode = 0; // Disable phantom wakes
                cfg.MinProcessorState = 5; // Still allow idle downclocking on Ryzen so it doesn't overheat unnecessarily
                cfg.MaxProcessorState = 100;
                cfg.BoostMode = 2; // Aggressive
                cfg.BoostPolicy = 100;
                cfg.EnergyPerfPreference = 0; // Max Performance bias
                cfg.CoreParkingMinCores = 100;
                cfg.CoolingPolicy = 1;
                cfg.PcieLinkStatePowerMgmt = 0; // Off
                cfg.AhciLinkPowerMgmt = 0; // Active
                cfg.PrimaryNvmeIdleMs = 500;
                cfg.PrimaryNvmeLatTolMs = 0;
                cfg.UsbSelectiveSuspend = false;
                cfg.Usb3LinkPowerMgmt = 0;
                break;

            case PresetKind.CoolAndQuiet:
                cfg.PlanName = "xPowerPlan — Cool & Quiet";
                cfg.PlanDescription = $"Lower heat & fan noise on {hw.CpuName} while keeping S3 sleep & xTaskForge media support.";
                cfg.DisplayOffMinutes = 8;
                cfg.SleepAfterMinutes = 12;
                cfg.WakeTimersMode = 0;
                cfg.MinProcessorState = 5;
                cfg.MaxProcessorState = 100;
                cfg.BoostMode = 3; // Efficient Enabled
                cfg.BoostPolicy = 60;
                cfg.EnergyPerfPreference = 55; // Balanced-eco
                cfg.CoreParkingMinCores = 50; // Allow parking up to half the cores when idle
                cfg.CoolingPolicy = 1;
                cfg.PcieLinkStatePowerMgmt = 1; // Moderate
                cfg.AhciLinkPowerMgmt = 2; // HIPM+DIPM
                cfg.PrimaryNvmeIdleMs = 100;
                cfg.PrimaryNvmeLatTolMs = 25;
                cfg.UsbSelectiveSuspend = !hw.HasExternalUsbDrive;
                cfg.Usb3LinkPowerMgmt = hw.HasExternalUsbDrive ? 1 : 2;
                break;

            default:
                cfg.PlanName = "xPowerPlan — Custom";
                break;
        }

        return cfg;
    }

    /// <summary>
    /// Audits the currently active Windows power plan against the detected hardware.
    /// </summary>
    public List<PowerAuditFinding> AuditActivePlan(PowerHardwareProfile hw, Dictionary<Guid, uint> activeValues)
    {
        var findings = new List<PowerAuditFinding>();
        var activeName = hw.ActiveScheme?.Name ?? "Unknown";

        // 1. Dead AMD iGPU / legacy AMD chipset plan check
        if (hw.HasDisabledAmdIgpu && hw.HasNvidiaGpu && activeName.Contains("AMD", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new PowerAuditFinding
            {
                Severity = PowerFindingSeverity.Warning,
                Category = "GPU / Plan",
                Title = "AMD Power Plan active with AMD iGPU disabled & NVIDIA GPU installed",
                Detail = $"Your CPU ({hw.CpuName}) has its Radeon iGPU disabled in BIOS and you're running {hw.Gpus.First().Name}. '{activeName}' carries dead Switchable Dynamic Graphics & AMD Graphics Power subgroups that don't apply to your rig."
            });
        }
        else if (hw.ActiveScheme?.IsManagedPlan == true)
        {
            findings.Add(new PowerAuditFinding
            {
                Severity = PowerFindingSeverity.Good,
                Category = "Plan",
                Title = $"Managed Power Plan is active ({activeName})",
                Detail = "Your custom hardware-tuned plan is currently enforced natively by the Windows kernel with zero background overhead."
            });
        }

        // 2. PCIe ASPM on NVIDIA + NVMe
        if (activeValues.TryGetValue(PowerGuids.PcieLinkStatePowerMgmt, out var pcie) && pcie != 0 && (hw.HasNvidiaGpu || hw.HasNvmeSsd))
        {
            findings.Add(new PowerAuditFinding
            {
                Severity = PowerFindingSeverity.Warning,
                Category = "PCIe / GPU",
                Title = $"PCIe Link State Power Management is set to {FormatPcie(pcie)}",
                Detail = "On a desktop with an NVIDIA discrete GPU and NVMe SSD, PCIe ASPM can introduce micro-stutter and wake latency during bursty IDE/AI/gaming workloads. Setting it to Off is recommended."
            });
        }
        else
        {
            findings.Add(new PowerAuditFinding
            {
                Severity = PowerFindingSeverity.Good,
                Category = "PCIe / GPU",
                Title = "PCIe Link State Power Management is Off",
                Detail = "Zero PCIe link wake latency for your discrete GPU and NVMe SSD."
            });
        }

        // 3. USB Selective Suspend with External USB Drive
        if (hw.HasExternalUsbDrive && activeValues.TryGetValue(PowerGuids.UsbSelectiveSuspend, out var usb) && usb == 1)
        {
            var usbDrive = hw.Disks.First(d => d.BusType == "USB").FriendlyName;
            findings.Add(new PowerAuditFinding
            {
                Severity = PowerFindingSeverity.Warning,
                Category = "USB / Storage",
                Title = $"USB Selective Suspend is Enabled with '{usbDrive}' attached",
                Detail = "Windows can suspend USB hubs and cause reconnects or spin-up pauses on your external USB 3.0 drive and USB audio peripherals. Disabling USB Selective Suspend is safer on a desktop."
            });
        }

        // 4. Hard Disk spin-down timeout
        if (activeValues.TryGetValue(PowerGuids.DiskTurnOffAfter, out var diskSec) && diskSec > 0)
        {
            findings.Add(new PowerAuditFinding
            {
                Severity = PowerFindingSeverity.Warning,
                Category = "Storage",
                Title = $"Turn off hard disk after {diskSec / 60} min is enabled",
                Detail = "With NVMe + SATA SSDs + external USB storage, aggressive disk turn-off causes unnecessary wake delays and drive head-parking wear. Setting to Never (0) is cleaner."
            });
        }

        // 5. xTaskForge / Media Keep-Awake compatibility
        var sysReq = activeValues.TryGetValue(PowerGuids.AllowSystemRequiredPolicy, out var sr) ? sr : 1;
        var dispReq = activeValues.TryGetValue(PowerGuids.AllowDisplayRequiredPolicy, out var dr) ? dr : 1;
        if (sysReq == 1 && dispReq == 1)
        {
            findings.Add(new PowerAuditFinding
            {
                Severity = PowerFindingSeverity.Good,
                Category = "Sleep / xTaskForge",
                Title = "Media & xTaskForge keep-awake policy is Enabled",
                Detail = "AllowSystemRequiredPolicy & AllowDisplayRequiredPolicy are ON — your PC will sleep normally when idle, while respecting xTaskForge / music / video keep-awake requests."
            });
        }
        else
        {
            findings.Add(new PowerAuditFinding
            {
                Severity = PowerFindingSeverity.Warning,
                Category = "Sleep / xTaskForge",
                Title = "System/Display Required Policy is Disabled",
                Detail = "Disabling this breaks xTaskForge and media apps from preventing sleep during music or video playback. Applying a tuned plan will re-enable it."
            });
        }

        // 6. Wake timers
        if (activeValues.TryGetValue(PowerGuids.AllowWakeTimers, out var wt) && wt == 1)
        {
            findings.Add(new PowerAuditFinding
            {
                Severity = PowerFindingSeverity.Warning,
                Category = "Sleep",
                Title = "All Wake Timers are Enabled",
                Detail = "Background scheduled tasks can randomly wake your PC from S3 sleep. Switching to 'Important Wake Timers Only' or 'Disable' stops phantom wakes."
            });
        }

        // 7. Hybrid Sleep blocked by Hypervisor
        if (hw.HypervisorBlocksHybridSleep && activeValues.TryGetValue(PowerGuids.AllowHybridSleep, out var hs) && hs == 1)
        {
            findings.Add(new PowerAuditFinding
            {
                Severity = PowerFindingSeverity.Info,
                Category = "Sleep",
                Title = "Hybrid Sleep is turned On, but Hypervisor disables it",
                Detail = "Your system supports clean S3 Standby while Hypervisor blocks Hybrid Sleep. Turning Allow Hybrid Sleep Off keeps the plan cleanly aligned with your firmware."
            });
        }

        // 8. Ryzen EPP & Boost
        if (hw.IsAmdCpu && activeValues.TryGetValue(PowerGuids.EnergyPerfPreference, out var epp) && epp == 0)
        {
            findings.Add(new PowerAuditFinding
            {
                Severity = PowerFindingSeverity.Info,
                Category = "CPU / Ryzen",
                Title = "Ryzen EPP is pinned to 0% (Maximum Performance bias)",
                Detail = "EPP=0% keeps boost clocks eager even on light background threads, increasing idle heat & fan ramp. For Smart Dev & AI, EPP=20% with Efficient Aggressive boost gives snappy bursts with cooler idle."
            });
        }

        return findings;
    }

    /// <summary>
    /// Converts a PowerPlanConfig into the exact ordered list of Windows power settings to enforce.
    /// </summary>
    public List<PowerSettingEntry> BuildSettingEntries(PowerPlanConfig cfg)
    {
        return new List<PowerSettingEntry>
        {
            // Sleep & Display (xTaskForge-friendly)
            new("Display", "Turn off display after", PowerGuids.SubDisplay, PowerGuids.DisplayTurnOffAfter,
                (uint)(cfg.DisplayOffMinutes * 60), "Idle monitor sleep timer", FormatMinutesFromSec),
            new("Display", "Allow display required policy", PowerGuids.SubDisplay, PowerGuids.AllowDisplayRequiredPolicy,
                cfg.RespectMediaKeepAwake ? 1u : 0u, "Lets video players & xTaskForge keep screen awake", FormatOnOff),
            new("Sleep", "Sleep after (S3 Standby)", PowerGuids.SubSleep, PowerGuids.SleepAfter,
                (uint)(cfg.SleepAfterMinutes * 60), "Sleeps PC cleanly when truly idle", FormatMinutesFromSec),
            new("Sleep", "Allow system required policy", PowerGuids.SubSleep, PowerGuids.AllowSystemRequiredPolicy,
                cfg.RespectMediaKeepAwake ? 1u : 0u, "Required so xTaskForge / music / video prevent sleep", FormatOnOff),
            new("Sleep", "Allow Standby States (S3)", PowerGuids.SubSleep, PowerGuids.AllowStandbyStates,
                1u, "Ensures S3 sleep works reliably", FormatOnOff),
            new("Sleep", "Hibernate after", PowerGuids.SubSleep, PowerGuids.HibernateAfter,
                (uint)(cfg.HibernateAfterMinutes * 60), "0 (Never) saves SSD write wear on desktop", FormatMinutesFromSec),
            new("Sleep", "Allow hybrid sleep", PowerGuids.SubSleep, PowerGuids.AllowHybridSleep,
                cfg.AllowHybridSleep ? 1u : 0u, "Matches Hypervisor S3 capability", FormatOnOff),
            new("Sleep", "Allow wake timers", PowerGuids.SubSleep, PowerGuids.AllowWakeTimers,
                (uint)cfg.WakeTimersMode, "Prevents random background wakeups", FormatWakeTimers),
            new("Security", "Require password on wakeup", PowerGuids.SubNone, PowerGuids.RequirePasswordOnWakeup,
                cfg.RequirePasswordOnWakeup ? 1u : 0u, "Lock screen on resume from sleep", FormatOnOff),

            // CPU (Ryzen / CPPC2)
            new("CPU", "Minimum processor state", PowerGuids.SubProcessor, PowerGuids.MinProcessorState,
                (uint)Math.Clamp(cfg.MinProcessorState, 0, 100), "Low floor lets CPPC2 drop clocks & heat at idle", FormatPercent),
            new("CPU", "Maximum processor state", PowerGuids.SubProcessor, PowerGuids.MaxProcessorState,
                (uint)Math.Clamp(cfg.MaxProcessorState, 5, 100), "Full boost ceiling under load", FormatPercent),
            new("CPU", "Processor performance boost mode", PowerGuids.SubProcessor, PowerGuids.PerfBoostMode,
                (uint)cfg.BoostMode, "Controls Ryzen turbo boost aggressiveness", FormatBoostMode),
            new("CPU", "Processor performance boost policy", PowerGuids.SubProcessor, PowerGuids.PerfBoostPolicy,
                (uint)Math.Clamp(cfg.BoostPolicy, 0, 100), "Headroom allowed for turbo boost", FormatPercent),
            new("CPU", "Energy Performance Preference (EPP)", PowerGuids.SubProcessor, PowerGuids.EnergyPerfPreference,
                (uint)Math.Clamp(cfg.EnergyPerfPreference, 0, 100), "0%=Max Perf, 20%=Snappy Dev, 55%=Cool", FormatPercent),
            new("CPU", "Core parking min cores", PowerGuids.SubProcessor, PowerGuids.CoreParkingMinCores,
                (uint)Math.Clamp(cfg.CoreParkingMinCores, 0, 100), "100% avoids core-unpark latency spikes", FormatPercent),
            new("CPU", "System cooling policy", PowerGuids.SubProcessor, PowerGuids.SystemCoolingPolicy,
                (uint)cfg.CoolingPolicy, "Active cools with fan before throttling CPU", FormatCooling),

            // GPU, PCIe, Storage & USB
            new("PCIe / GPU", "PCIe Link State Power Management", PowerGuids.SubPci, PowerGuids.PcieLinkStatePowerMgmt,
                (uint)cfg.PcieLinkStatePowerMgmt, "Off eliminates RTX GPU & NVMe PCIe wake latency", FormatPcie),
            new("Storage", "Turn off hard disk after", PowerGuids.SubDisk, PowerGuids.DiskTurnOffAfter,
                (uint)(cfg.DiskTurnOffMinutes * 60), "Never prevents USB/SATA wake stalls", FormatMinutesFromSec),
            new("Storage", "AHCI Link Power Management", PowerGuids.SubDisk, PowerGuids.AhciLinkPowerMgmt,
                (uint)cfg.AhciLinkPowerMgmt, "Active keeps SATA SSD latency snappy", FormatAhci),
            new("Storage", "Primary NVMe Idle Timeout", PowerGuids.SubDisk, PowerGuids.PrimaryNvmeIdleMs,
                (uint)Math.Max(0, cfg.PrimaryNvmeIdleMs), "Controls NVMe APST idle transition window", v => $"{v} ms"),
            new("Storage", "Primary NVMe Latency Tolerance", PowerGuids.SubDisk, PowerGuids.PrimaryNvmeLatTolMs,
                (uint)Math.Max(0, cfg.PrimaryNvmeLatTolMs), "Lower = faster NVMe wake from low-power state", v => $"{v} ms"),
            new("USB", "USB Selective Suspend", PowerGuids.SubUsb, PowerGuids.UsbSelectiveSuspend,
                cfg.UsbSelectiveSuspend ? 1u : 0u, "Off prevents external USB 3.0 & audio dropouts", FormatOnOff),
            new("USB", "USB 3 Link Power Management", PowerGuids.SubUsb, PowerGuids.Usb3LinkPowerMgmt,
                (uint)cfg.Usb3LinkPowerMgmt, "Off/Min keeps external USB 3.0 drives responsive", FormatUsb3),
            new("Network", "Wireless Adapter Power Saving", PowerGuids.SubWireless, PowerGuids.WirelessPowerSaving,
                (uint)cfg.WirelessPowerSaving, "Max Performance prevents Wi-Fi latency spikes", FormatWifi)
        };
    }

    public List<PowerDiffRow> ComputeDiff(PowerPlanConfig cfg, Dictionary<Guid, uint> currentActiveValues)
    {
        var entries = BuildSettingEntries(cfg);
        var rows = new List<PowerDiffRow>(entries.Count);

        foreach (var e in entries)
        {
            var hasCurrent = currentActiveValues.TryGetValue(e.SettingGuid, out var curVal);
            rows.Add(new PowerDiffRow
            {
                Category = e.Category,
                SettingName = e.Name,
                CurrentValue = hasCurrent ? e.Formatter(curVal) : "Not set",
                TargetValue = e.Formatter(e.TargetValue),
                IsChanged = !hasCurrent || curVal != e.TargetValue,
                Reason = e.Reason
            });
        }

        return rows;
    }

    /// <summary>
    /// Creates or updates the managed power scheme, enforces every setting, activates it, and verifies via /qh readback.
    /// </summary>
    public async Task<PowerApplyResult> GenerateAndApplyAsync(
        PowerPlanConfig cfg,
        PowerHardwareProfile hw,
        Guid? existingManagedGuid,
        string? backupBaseDir = null)
    {
        return await Task.Run(() =>
        {
            var res = new PowerApplyResult();
            void Log(string msg) => res.Logs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");

            // 1. Optional backup of currently active plan
            if (cfg.AutoBackupPreviousPlan && hw.ActiveScheme != null)
            {
                var backupDir = !string.IsNullOrWhiteSpace(backupBaseDir)
                    ? backupBaseDir
                    : Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "xPrograms", "xSecurityCenter", "Backups", "PowerPlans");
                var safeOld = string.Concat(hw.ActiveScheme.Name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ')).Trim().Replace(' ', '_');
                var backupFile = Path.Combine(backupDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safeOld}.pow");
                var (bkOk, bkOut) = _powerCfg.ExportScheme(hw.ActiveScheme.Guid, backupFile);
                if (bkOk)
                {
                    res.BackupPath = backupFile;
                    Log($"Backed up previous active plan '{hw.ActiveScheme.Name}' -> {backupFile}");
                }
                else
                {
                    Log($"Warning: backup failed ({bkOut.Trim()})");
                }
            }

            // 2. Find or create target scheme
            var schemes = _powerCfg.ListSchemes();
            Guid targetGuid = Guid.Empty;

            if (cfg.ReuseSingleManagedPlan)
            {
                if (existingManagedGuid.HasValue && schemes.Any(s => s.Guid == existingManagedGuid.Value))
                {
                    targetGuid = existingManagedGuid.Value;
                    Log($"Reusing existing managed power scheme ({targetGuid}).");
                }
                else
                {
                    var matchByPrefix = schemes.FirstOrDefault(s => s.IsManagedPlan);
                    if (matchByPrefix != null)
                    {
                        targetGuid = matchByPrefix.Guid;
                        Log($"Reusing existing scheme '{matchByPrefix.Name}' ({targetGuid}).");
                    }
                }
            }
            else
            {
                var matchByName = schemes.FirstOrDefault(s => s.Name.Equals(cfg.PlanName, StringComparison.OrdinalIgnoreCase));
                if (matchByName != null)
                {
                    targetGuid = matchByName.Guid;
                    Log($"Updating existing scheme '{matchByName.Name}' ({targetGuid}).");
                }
            }

            if (targetGuid == Guid.Empty)
            {
                var (dupOk, newGuid, dupOut) = _powerCfg.DuplicateScheme(PowerGuids.SchemeBalanced);
                if (!dupOk)
                {
                    res.Success = false;
                    res.Summary = $"Failed to create power scheme: {dupOut.Trim()}";
                    Log(res.Summary);
                    return res;
                }
                targetGuid = newGuid;
                Log($"Created clean power scheme from Windows Balanced base -> {targetGuid}");
            }

            // 3. Rename
            var (renOk, renOut) = _powerCfg.RenameScheme(targetGuid, cfg.PlanName, cfg.PlanDescription);
            Log(renOk ? $"Named scheme '{cfg.PlanName}'" : $"Rename warning: {renOut.Trim()}");

            // 4. Apply every setting
            var entries = BuildSettingEntries(cfg);
            foreach (var entry in entries)
            {
                var (ok, outMsg) = _powerCfg.SetSettingValue(targetGuid, entry.SubgroupGuid, entry.SettingGuid, entry.TargetValue);
                if (ok)
                {
                    res.SettingsApplied++;
                }
                else
                {
                    res.SettingsFailed++;
                    Log($"Skipped/failed '{entry.Name}': {outMsg.Trim()}");
                }
            }
            Log($"Applied {res.SettingsApplied}/{entries.Count} settings.");

            // 5. Optionally unhide useful advanced settings in Windows Control Panel
            if (cfg.UnhideUsefulAdvancedSettings)
            {
                var toUnhide = new (Guid Sub, Guid Set, string Label)[]
                {
                    (PowerGuids.SubProcessor, PowerGuids.PerfBoostMode, "Processor performance boost mode"),
                    (PowerGuids.SubProcessor, PowerGuids.EnergyPerfPreference, "Processor EPP"),
                    (PowerGuids.SubDisk, PowerGuids.PrimaryNvmeIdleMs, "Primary NVMe Idle Timeout")
                };
                int unhidden = 0;
                foreach (var item in toUnhide)
                {
                    var (uOk, _) = _powerCfg.UnhideSetting(item.Sub, item.Set);
                    if (uOk) unhidden++;
                }
                Log(unhidden > 0
                    ? $"Unhid {unhidden}/{toUnhide.Length} advanced settings in Windows Control Panel."
                    : "Note: Unhiding Windows Control Panel attributes requires Administrator privileges (plan settings still applied).");
            }

            // 6. Activate scheme
            var (actOk, actOut) = _powerCfg.SetActiveScheme(targetGuid);
            if (!actOk)
            {
                res.Success = false;
                res.Summary = $"Settings applied, but failed to activate scheme: {actOut.Trim()}";
                Log(res.Summary);
                return res;
            }
            Log($"Activated '{cfg.PlanName}' ({targetGuid}) as the active Windows power plan.");

            // 7. Read-back verification via /qh
            var readBack = _powerCfg.QueryAllAcValues(targetGuid);
            foreach (var entry in entries)
            {
                if (readBack.TryGetValue(entry.SettingGuid, out var actual) && actual == entry.TargetValue)
                {
                    res.SettingsVerified++;
                }
                else if (readBack.TryGetValue(entry.SettingGuid, out var mismatch))
                {
                    Log($"Verification mismatch on '{entry.Name}': expected {entry.Formatter(entry.TargetValue)}, got {entry.Formatter(mismatch)}");
                }
            }

            res.Success = true;
            res.AppliedSchemeGuid = targetGuid;
            res.AppliedSchemeName = cfg.PlanName;
            res.Summary = $"Enforced '{cfg.PlanName}' — {res.SettingsVerified}/{entries.Count} settings verified active.";
            Log(res.Summary);
            return res;
        });
    }

    // Formatters
    public static string FormatMinutesFromSec(uint sec) => sec == 0 ? "Never" : $"{sec / 60} min";
    public static string FormatOnOff(uint v) => v == 0 ? "Off" : "On";
    public static string FormatPercent(uint v) => $"{v}%";
    public static string FormatWakeTimers(uint v) => v switch { 0 => "Disable", 1 => "Enable", 2 => "Important Only", _ => v.ToString() };
    public static string FormatBoostMode(uint v) => v switch
    {
        0 => "Disabled",
        1 => "Enabled",
        2 => "Aggressive",
        3 => "Efficient Enabled",
        4 => "Efficient Aggressive",
        5 => "Aggressive At Guaranteed",
        6 => "Efficient Aggressive At Guaranteed",
        _ => v.ToString()
    };
    public static string FormatCooling(uint v) => v == 0 ? "Passive" : "Active";
    public static string FormatPcie(uint v) => v switch { 0 => "Off", 1 => "Moderate", 2 => "Maximum", _ => v.ToString() };
    public static string FormatAhci(uint v) => v switch { 0 => "Active", 1 => "HIPM", 2 => "HIPM+DIPM", 3 => "Lowest", 4 => "DIPM", _ => v.ToString() };
    public static string FormatUsb3(uint v) => v switch { 0 => "Off", 1 => "Minimum", 2 => "Moderate", 3 => "Maximum", _ => v.ToString() };
    public static string FormatWifi(uint v) => v switch { 0 => "Max Performance", 1 => "Low Saving", 2 => "Medium Saving", 3 => "Max Saving", _ => v.ToString() };
}
