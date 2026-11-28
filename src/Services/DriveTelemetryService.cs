using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

public static class DriveTelemetryService
{
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GetVolumePathName(string fileName, StringBuilder volumePathName, uint bufferLength);

    private static readonly Dictionary<int, (string Name, bool IsCritical, string Description, string Impact)> KnownSmartAttributes = new()
    {
        { 1, ("Raw Read Error Rate", false, "Frequency of hardware read errors occurring while reading from disk surface.", "Non-zero indicates failing head or disk surface.") },
        { 5, ("Reallocated Sectors Count", true, "Count of bad sectors that have been retired and remapped to spare sectors.", "#1 statistical predictor of drive failure. Any non-zero count means active surface/block degradation.") },
        { 9, ("Power-On Hours", false, "Total count of hours the drive has been in power-on state.", "Measures total operating age and mechanical/electrical wear.") },
        { 10, ("Spin Retry Count", true, "Count of retry attempts to spin up the spindle motor to full speed.", "Non-zero indicates mechanical spindle failure is imminent.") },
        { 12, ("Power Cycle Count", false, "Count of full power on/off cycles.", "Thermal and electrical stress indicator.") },
        { 171, ("SSD Program Fail Count", true, "Count of flash write/program operation failures.", "Indicates dying flash NAND blocks.") },
        { 172, ("SSD Erase Fail Count", true, "Count of flash block erase operation failures.", "Indicates dying flash NAND blocks.") },
        { 173, ("SSD Wear Leveling / Erase Count", false, "Worst and average block erase cycles.", "Measures wear leveling uniformity and flash endurance.") },
        { 174, ("Unexpected Power Loss Count", false, "Number of times power was cut without standby/flush notification.", "High counts can cause flash metadata corruption.") },
        { 180, ("Unused Reserved Block Count", true, "Remaining factory spare flash blocks.", "When this hits zero, drive will fail on subsequent bad blocks.") },
        { 184, ("End-to-End Error / Parity Error", true, "Data parity errors between host and disk controller cache.", "Indicates corrupted internal controller memory.") },
        { 187, ("Reported Uncorrectable Errors", true, "Read errors that could not be recovered using ECC.", "Critical predictor of failure. Data unrecoverable.") },
        { 188, ("Command Timeout", true, "Number of aborted commands due to drive communication timeout.", "Indicates drive or controller freezing.") },
        { 190, ("Airflow Temperature", false, "Internal airflow temperature in Celsius.", "Thermal monitoring.") },
        { 194, ("Disk Temperature", false, "Current internal physical sensor temperature.", "Overheating drastically shortens drive lifespan.") },
        { 196, ("Reallocation Event Count", true, "Count of remapping attempts (both successful and failed).", "Surface degradation indicator.") },
        { 197, ("Current Pending Sector Count", true, "Count of unstable sectors waiting to be remapped on write.", "Strong predictor of drive failure. Immediate backup required.") },
        { 198, ("Offline Uncorrectable Sectors", true, "Count of uncorrectable errors during background offline scans.", "Surface unreadable; failure imminent.") },
        { 199, ("UltraDMA CRC Error Count", false, "Count of data transmission errors over interface cable.", "Usually caused by damaged SATA cable or port.") },
        { 202, ("Percentage Lifetime Used / Endurance", true, "Percentage of total NAND flash write endurance consumed.", "Reaching 100% means write endurance exhausted.") },
        { 231, ("SSD Life Left", true, "Percentage of endurance life remaining.", "Critical lifespan metric.") },
        { 232, ("Available Reserved Space", true, "Percentage of spare blocks remaining.", "Spare capacity health.") },
        { 233, ("Media Wearout Indicator", true, "NAND wear indicator.", "Flash block wear.") },
        { 241, ("Total LBAs Written (TBW)", false, "Total 512-byte logical sectors written by host.", "Measures total data written against manufacturer TBW rating.") },
        { 242, ("Total LBAs Read (TBR)", false, "Total 512-byte logical sectors read by host.", "Total read traffic.") }
    };

    public static List<PhysicalDriveInfo> GetAllPhysicalDrives()
    {
        var drives = new List<PhysicalDriveInfo>();
        var volumeMap = BuildVolumeToDiskMap();

        // 1. Query MSFT_Disk from root\Microsoft\Windows\Storage for BusType and Location (VHDX backing file)
        var msftDisks = StorageInterop.ComWmi.Query("root\\Microsoft\\Windows\\Storage",
            "SELECT Number, FriendlyName, BusType, Location FROM MSFT_Disk");
        var msftDiskMap = new Dictionary<int, Dictionary<string, object?>>();
        foreach (var md in msftDisks)
        {
            if (md.TryGetValue("Number", out var numObj) && numObj != null && int.TryParse(numObj.ToString(), out var num))
            {
                msftDiskMap[num] = md;
            }
        }

        // 2. Query Win32_DiskDrive via COM reflection
        var wmiDisks = StorageInterop.ComWmi.Query("root\\cimv2",
            "SELECT Index, DeviceID, Model, SerialNumber, Size, MediaType, InterfaceType, PNPDeviceID FROM Win32_DiskDrive");

        var discoveredIndices = new HashSet<int>();
        foreach (var wmi in wmiDisks)
        {
            if (wmi.TryGetValue("Index", out var idxObj) && idxObj != null && int.TryParse(idxObj.ToString(), out var index))
            {
                discoveredIndices.Add(index);
                msftDiskMap.TryGetValue(index, out var msftData);
                var drive = QuerySingleDrive(index, wmi, msftData, volumeMap);
                if (drive != null) drives.Add(drive);
            }
        }

        // 3. Fallback probe for any PhysicalDrive index 0..7 not enumerated by WMI
        for (var i = 0; i < 8; i++)
        {
            if (!discoveredIndices.Contains(i))
            {
                msftDiskMap.TryGetValue(i, out var msftData);
                var drive = QuerySingleDrive(i, null, msftData, volumeMap);
                if (drive != null && drive.SizeBytes > 0)
                {
                    drives.Add(drive);
                }
            }
        }

        return drives.OrderBy(d => d.DiskIndex).ToList();
    }

    private static Dictionary<int, List<DriveRow>> BuildVolumeToDiskMap()
    {
        var map = new Dictionary<int, List<DriveRow>>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                var mapping = StorageInterop.GetVolumeDiskNumber(drive.Name);
                if (mapping.HasValue)
                {
                    var diskNum = mapping.Value.DiskNumber;
                    if (!map.ContainsKey(diskNum)) map[diskNum] = new List<DriveRow>();

                    var ready = drive.IsReady;
                    var total = ready ? drive.TotalSize : 0;
                    var free = ready ? drive.AvailableFreeSpace : 0;
                    var label = ready && !string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.VolumeLabel : (ready ? "Local volume" : "Not ready");

                    map[diskNum].Add(new DriveRow
                    {
                        Name = drive.Name,
                        Label = label,
                        TypeLabel = drive.DriveType.ToString(),
                        CapacityLabel = ready ? FormatBytes(total) : "â€”",
                        FreeLabel = ready ? FormatBytes(free) : "â€”",
                        FileSystem = ready ? SafeFormat(drive) : "â€”",
                        RootPath = drive.Name,
                        DriveType = drive.DriveType,
                        IsReady = ready,
                        TotalBytes = total,
                        FreeBytes = free
                    });
                }
            }
            catch
            {
                // Ignore volume query errors
            }
        }
        return map;
    }

    private static string SafeFormat(DriveInfo d)
    {
        try { return d.DriveFormat; } catch { return "â€”"; }
    }

    public static PhysicalDriveInfo? QuerySingleDrive(
        int diskIndex,
        Dictionary<string, object?>? wmiData,
        Dictionary<string, object?>? msftData = null,
        Dictionary<int, List<DriveRow>>? volumeMap = null)
    {
        if (msftData == null)
        {
            var msftQuery = StorageInterop.ComWmi.Query("root\\Microsoft\\Windows\\Storage",
                $"SELECT Number, FriendlyName, BusType, Location FROM MSFT_Disk WHERE Number = {diskIndex}");
            if (msftQuery.Count > 0) msftData = msftQuery[0];
        }

        using var handle = StorageInterop.OpenDrive(diskIndex);
        if (handle.IsInvalid && wmiData == null && msftData == null) return null;

        var drive = new PhysicalDriveInfo
        {
            DiskIndex = diskIndex,
            LastUpdated = DateTime.Now
        };

        // Attached volumes
        volumeMap ??= BuildVolumeToDiskMap();
        if (volumeMap.TryGetValue(diskIndex, out var volList))
        {
            drive.MountedVolumes = volList.Select(v => v.Name.TrimEnd('\\')).ToList();
            drive.VolumesSummary = string.Join(", ", volList.Select(v => $"{v.Name.TrimEnd('\\')} ({v.Label}, {v.CapacityLabel})"));
        }

        // Physical size
        var sizeBytes = StorageInterop.GetDiskSizeBytes(handle);
        if (sizeBytes == 0 && wmiData != null && wmiData.TryGetValue("Size", out var sizeObj) && sizeObj != null && long.TryParse(sizeObj.ToString(), out var wmiSize))
        {
            sizeBytes = wmiSize;
        }
        drive.SizeBytes = sizeBytes;
        drive.SizeLabel = sizeBytes > 0 ? FormatBytes(sizeBytes) : "â€”";

        // Query Device Descriptor
        var (vendor, product, serial, busTypeInt) = StorageInterop.GetDeviceDescriptor(handle);
        drive.Vendor = vendor;
        drive.Model = !string.IsNullOrWhiteSpace(product)
            ? product
            : (wmiData?["Model"]?.ToString() ?? msftData?["FriendlyName"]?.ToString() ?? $"Disk {diskIndex}");
        drive.SerialNumber = !string.IsNullOrWhiteSpace(serial)
            ? serial
            : (wmiData?["SerialNumber"]?.ToString()?.Trim() ?? "Unavailable");

        // MSFT Storage Metadata (BusType & Location)
        var location = msftData?["Location"]?.ToString() ?? "";
        var msftBusType = 0;
        if (msftData != null && msftData.TryGetValue("BusType", out var btObj) && btObj != null)
        {
            int.TryParse(btObj.ToString(), out msftBusType);
        }

        // Bus type
        drive.BusType = busTypeInt switch
        {
            17 => "NVMe",
            11 => "SATA",
            7 => "USB",
            3 => "SCSI",
            8 => "RAID",
            1 => "SCSI",
            _ => msftBusType switch
            {
                17 => "NVMe",
                15 => "Virtual (VHDX)",
                11 => "SATA",
                7 => "USB",
                _ => wmiData?["InterfaceType"]?.ToString() ?? "Unknown"
            }
        };

        // Check for File-Backed Virtual Disk (e.g. C:\Repositories.vhdx or BackupDev.vhdx)
        var isVirtual = msftBusType == 15
            || location.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase)
            || location.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)
            || drive.Model.Contains("Virtual Disk", StringComparison.OrdinalIgnoreCase)
            || (wmiData?["PNPDeviceID"]?.ToString() ?? "").Contains("VIRTUAL_DISK", StringComparison.OrdinalIgnoreCase);

        if (isVirtual)
        {
            drive.IsVirtualDisk = true;
            drive.BusType = "Virtual (VHDX)";
            drive.MediaType = "Virtual Disk (VHDX)";
            if (string.IsNullOrWhiteSpace(drive.Model) || drive.Model.StartsWith("Disk "))
            {
                drive.Model = "Microsoft Virtual Disk";
            }
        }
        else if (drive.BusType == "NVMe" || drive.Model.Contains("NVMe", StringComparison.OrdinalIgnoreCase) || drive.Model.Contains("PCIe SSD", StringComparison.OrdinalIgnoreCase))
        {
            drive.MediaType = "NVMe SSD";
            drive.BusType = "NVMe";
        }
        else
        {
            var seekPenalty = StorageInterop.GetSeekPenalty(handle);
            if (seekPenalty.HasValue)
            {
                drive.MediaType = seekPenalty.Value ? "Hard Disk Drive (HDD)" : "SATA SSD";
            }
            else if (wmiData?["MediaType"]?.ToString() is { } mediaTypeStr && mediaTypeStr.Contains("SSD", StringComparison.OrdinalIgnoreCase))
            {
                drive.MediaType = "SATA SSD";
            }
            else
            {
                drive.MediaType = drive.BusType == "USB" ? "External USB Drive" : "Fixed Disk";
            }
        }

        // Query telemetry if not a virtual disk
        if (!drive.IsVirtualDisk)
        {
            // Direct device temperature
            drive.TemperatureC = StorageInterop.QueryDeviceTemperature(handle);

            // 1. Try NVMe Telemetry via IOCTL_STORAGE_QUERY_PROPERTY
            var nvmeLog = StorageInterop.QueryNvmeHealthLog(handle);
            if (nvmeLog != null && nvmeLog.Length >= 512)
            {
                ParseNvmeHealth(drive, nvmeLog);
            }
            else
            {
                // 2. Query ATA IDENTIFY (SAT-12, SAT-16, ATA pass-through)
                // Recovers true model (e.g. TOSHIBA MQ04UBD200), serial, and RPM behind USB bridges
                var satIdentify = StorageInterop.QuerySatIdentify(handle);
                if (satIdentify != null && satIdentify.Length >= 512)
                {
                    ParseAtaIdentify(drive, satIdentify);
                }

                // 3. Query ATA SMART (SAT-12, SAT-16, ATA pass-through)
                if (StorageInterop.QuerySatSmart(handle, out var satData, out var satThresholds))
                {
                    drive.HasSatPassThrough = true;
                    ParseAtaSmartBuffer(drive, satData!, satThresholds);
                }
                else
                {
                    // 4. SATA / ATA / WMI SMART fallback
                    ParseAtaSmart(drive, wmiData);
                }
            }

            // 4. If temperature is still unknown, try SCSI LOG SENSE (Page 0x0D)
            if (!drive.TemperatureC.HasValue)
            {
                var scsiTemp = StorageInterop.QueryScsiTemperature(handle);
                if (scsiTemp.HasValue) drive.TemperatureC = scsiTemp.Value;
            }
        }

        // Query correlated storage event logs
        drive.StorageEvents = QueryRecentStorageEvents(diskIndex);

        // Historical Trend & Failure Prediction
        var trend = TelemetryHistoryManager.Instance.CalculateTrend(drive);
        drive.Prediction = FailurePredictionEngine.Evaluate(drive, trend);
        drive.HealthScore = FailurePredictionEngine.CalculateHealthScore(drive, drive.Prediction);

        drive.HealthStatus = drive.Prediction.Severity switch
        {
            FailureSeverity.CriticalImmediate => "Critical",
            FailureSeverity.Warning30Days => "Failure Predicted",
            FailureSeverity.AttentionNeeded => "Attention Needed",
            _ when drive.IsVirtualDisk => "Healthy (Virtual Disk)",
            _ when !drive.HasHardwareHealthTelemetry => "Telemetry Limited",
            _ => "Healthy"
        };

        // Save history snapshot
        TelemetryHistoryManager.Instance.Record(drive);

        return drive;
    }

    private static void ParseNvmeHealth(PhysicalDriveInfo drive, byte[] data)
    {
        drive.MediaType = "NVMe SSD";
        drive.BusType = "NVMe";

        // Critical Warning (byte 0)
        var critWarn = data[0];
        // Composite Temperature (bytes 1-2, Kelvin)
        var tempK = BitConverter.ToUInt16(data, 1);
        if (tempK > 200 && tempK < 400)
        {
            drive.TemperatureC = tempK - 273;
        }

        // Available Spare (byte 3)
        var availSpare = data[3];
        drive.AvailableSparePercent = availSpare;

        // Available Spare Threshold (byte 4)
        var spareThresh = data[4];

        // Percentage Used (byte 5)
        var percentUsed = data[5];
        drive.LifeRemainingPercent = Math.Max(0, 100 - percentUsed);

        // Data Units Read (bytes 32-47, thousands of 512-byte blocks)
        var durLow = BitConverter.ToUInt64(data, 32);
        drive.TotalBytesRead = durLow * 1000UL * 512UL;

        // Data Units Written (bytes 48-63)
        var duwLow = BitConverter.ToUInt64(data, 48);
        drive.TotalBytesWritten = duwLow * 1000UL * 512UL;

        // Host Read Commands (bytes 64-79)
        // Host Write Commands (bytes 80-95)
        // Controller Busy Time (bytes 96-111, minutes)

        // Power Cycles (bytes 112-127)
        drive.PowerCycles = (long)BitConverter.ToUInt64(data, 112);

        // Power On Hours (bytes 128-143)
        drive.PowerOnHours = (long)BitConverter.ToUInt64(data, 128);

        // Unsafe Shutdowns (bytes 144-159)
        drive.UnsafeShutdowns = BitConverter.ToUInt64(data, 144);

        // Media and Data Integrity Errors (bytes 160-175)
        drive.MediaErrors = BitConverter.ToUInt64(data, 160);

        // Error Information Log Entry Count (bytes 176-191)
        var errorLogEntries = BitConverter.ToUInt64(data, 176);

        // Build SMART/NVMe attribute items
        drive.SmartAttributes.Add(new SmartAttributeItem
        {
            Id = 1,
            Name = "Critical Warning",
            Current = critWarn,
            Threshold = 0,
            RawValue = critWarn,
            RawDisplay = critWarn == 0 ? "0 (Normal)" : $"0x{critWarn:X2} (Flags set!)",
            Status = critWarn == 0 ? "Good" : "Critical",
            IsCritical = true,
            Description = "Controller status bitmask indicating critical drive failure states.",
            Impact = "Any active bit indicates severe danger to data integrity."
        });

        drive.SmartAttributes.Add(new SmartAttributeItem
        {
            Id = 2,
            Name = "Composite Temperature",
            Current = drive.TemperatureC ?? 0,
            Threshold = 75,
            RawValue = tempK,
            RawDisplay = $"{drive.TemperatureC}Â°C ({tempK} K)",
            Status = (drive.TemperatureC ?? 0) >= 75 ? "Warning" : "Good",
            IsCritical = false,
            Description = "Real-time controller and NAND composite operating temperature.",
            Impact = "High heat accelerates silicon degradation."
        });

        drive.SmartAttributes.Add(new SmartAttributeItem
        {
            Id = 3,
            Name = "Available Spare",
            Current = availSpare,
            Worst = availSpare,
            Threshold = spareThresh,
            RawValue = availSpare,
            RawDisplay = $"{availSpare}% (Threshold: {spareThresh}%)",
            Status = availSpare <= spareThresh ? "Critical" : (availSpare <= 20 ? "Warning" : "Good"),
            IsCritical = true,
            Description = "Percentage of remaining reserve flash blocks available to replace degraded blocks.",
            Impact = "When spare blocks run out, the drive locks into read-only mode to prevent data corruption."
        });

        drive.SmartAttributes.Add(new SmartAttributeItem
        {
            Id = 5,
            Name = "Percentage Used (Wear Indicator)",
            Current = percentUsed,
            Worst = percentUsed,
            Threshold = 100,
            RawValue = percentUsed,
            RawDisplay = $"{percentUsed}% wear ({100 - percentUsed}% life remaining)",
            Status = percentUsed >= 95 ? "Critical" : (percentUsed >= 85 ? "Warning" : "Good"),
            IsCritical = true,
            Description = "Manufacturer estimate of percentage of drive endurance life consumed.",
            Impact = "Reaching 100% means write endurance specification has been fully used."
        });

        drive.SmartAttributes.Add(new SmartAttributeItem
        {
            Id = 160,
            Name = "Media and Data Integrity Errors",
            Current = (int)Math.Min(int.MaxValue, drive.MediaErrors ?? 0),
            Threshold = 0,
            RawValue = (long)(drive.MediaErrors ?? 0),
            RawDisplay = (drive.MediaErrors ?? 0).ToString("N0"),
            Status = (drive.MediaErrors ?? 0) > 0 ? "Critical" : "Good",
            IsCritical = true,
            Description = "Number of occurrences where unrecoverable data integrity errors (ECC failures) occurred.",
            Impact = "Any non-zero count means uncorrectable NAND degradation. Immediate backup recommended."
        });

        drive.SmartAttributes.Add(new SmartAttributeItem
        {
            Id = 176,
            Name = "Error Information Log Entries",
            Current = (int)Math.Min(int.MaxValue, errorLogEntries),
            Threshold = 0,
            RawValue = (long)errorLogEntries,
            RawDisplay = errorLogEntries.ToString("N0"),
            Status = errorLogEntries > 10 ? "Warning" : "Good",
            IsCritical = false,
            Description = "Number of Error Information log entries logged by the controller.",
            Impact = "Reflects communication, timeout, or internal hardware errors."
        });

        drive.SmartAttributes.Add(new SmartAttributeItem
        {
            Id = 128,
            Name = "Power-On Hours",
            Current = (int)Math.Min(int.MaxValue, drive.PowerOnHours ?? 0),
            Threshold = 0,
            RawValue = drive.PowerOnHours ?? 0,
            RawDisplay = drive.PowerOnHoursLabel,
            Status = "Good",
            IsCritical = false,
            Description = "Total operating hours the drive has been powered on.",
            Impact = "Assesses operational age."
        });

        drive.SmartAttributes.Add(new SmartAttributeItem
        {
            Id = 144,
            Name = "Unsafe Shutdowns",
            Current = (int)Math.Min(int.MaxValue, drive.UnsafeShutdowns ?? 0),
            Threshold = 0,
            RawValue = (long)(drive.UnsafeShutdowns ?? 0),
            RawDisplay = (drive.UnsafeShutdowns ?? 0).ToString("N0"),
            Status = "Good",
            IsCritical = false,
            Description = "Number of times power was lost without a graceful shutdown notification.",
            Impact = "Can cause flash metadata desynchronization."
        });

        drive.SmartAttributes.Add(new SmartAttributeItem
        {
            Id = 48,
            Name = "Data Units Written (TBW)",
            Current = 0,
            Threshold = 0,
            RawValue = (long)(drive.TotalBytesWritten ?? 0),
            RawDisplay = drive.TotalBytesWrittenLabel,
            Status = "Good",
            IsCritical = false,
            Description = "Total data written to NAND flash over the lifetime of the drive.",
            Impact = "Total write workload comparison against drive endurance rating."
        });
    }

    private static void ParseAtaSmart(PhysicalDriveInfo drive, Dictionary<string, object?>? wmiData)
    {
        // Query WMI MSStorageDriver_FailurePredictData
        var pnp = wmiData?["PNPDeviceID"]?.ToString() ?? "";
        var smartItems = StorageInterop.ComWmi.Query("root\\wmi", "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData");
        var threshItems = StorageInterop.ComWmi.Query("root\\wmi", "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictThresholds");

        byte[]? smartBytes = null;
        byte[]? threshBytes = null;

        foreach (var item in smartItems)
        {
            var inst = item["InstanceName"]?.ToString() ?? "";
            var matchesPnp = !string.IsNullOrWhiteSpace(pnp) && inst.StartsWith(pnp, StringComparison.OrdinalIgnoreCase);
            var matchesModel = !string.IsNullOrWhiteSpace(drive.Model) && inst.Contains(drive.Model, StringComparison.OrdinalIgnoreCase);

            if (matchesPnp || matchesModel)
            {
                smartBytes = item["VendorSpecific"] as byte[];
                break;
            }
        }

        foreach (var item in threshItems)
        {
            var inst = item["InstanceName"]?.ToString() ?? "";
            var matchesPnp = !string.IsNullOrWhiteSpace(pnp) && inst.StartsWith(pnp, StringComparison.OrdinalIgnoreCase);
            var matchesModel = !string.IsNullOrWhiteSpace(drive.Model) && inst.Contains(drive.Model, StringComparison.OrdinalIgnoreCase);

            if (matchesPnp || matchesModel)
            {
                threshBytes = item["VendorSpecific"] as byte[];
                break;
            }
        }

        if (smartBytes != null && smartBytes.Length >= 12)
        {
            ParseAtaSmartBuffer(drive, smartBytes, threshBytes);
        }
    }

    private static void ParseAtaSmartBuffer(PhysicalDriveInfo drive, byte[] smartBytes, byte[]? threshBytes)
    {
        if (smartBytes == null || smartBytes.Length < 12) return;

        // Parse ATA SMART attributes (12 bytes per attribute, starting at index 2)
        for (var i = 2; i + 11 < smartBytes.Length; i += 12)
        {
            var id = (int)smartBytes[i];
            if (id == 0) continue;

            var curr = (int)smartBytes[i + 3];
            var worst = (int)smartBytes[i + 4];
            var rawVal = BitConverter.ToUInt32(smartBytes, i + 5);

            // 6-byte raw value
            long raw64 = 0;
            for (var b = 5; b >= 0; b--)
            {
                raw64 = (raw64 << 8) | smartBytes[i + 5 + b];
            }

            // Lookup threshold
            var threshVal = 0;
            if (threshBytes != null)
            {
                for (var ti = 2; ti + 11 < threshBytes.Length; ti += 12)
                {
                    if ((int)threshBytes[ti] == id)
                    {
                        threshVal = threshBytes[ti + 1];
                        break;
                    }
                }
            }

            var (name, isCrit, desc, impact) = KnownSmartAttributes.TryGetValue(id, out var info)
                ? info
                : ($"Vendor Attribute {id}", false, "Vendor-specific telemetry.", "");

            var status = "Good";
            if (threshVal > 0 && curr <= threshVal) status = "Critical";
            else if (isCrit && rawVal > 0 && (id == 5 || id == 197 || id == 198 || id == 187)) status = "Warning";

            drive.SmartAttributes.Add(new SmartAttributeItem
            {
                Id = id,
                Name = name,
                Current = curr,
                Worst = worst,
                Threshold = threshVal,
                RawValue = (long)rawVal,
                RawDisplay = FormatSmartRaw(id, rawVal),
                Status = status,
                IsCritical = isCrit,
                Description = desc,
                Impact = impact
            });

            // Map critical known metrics to physical drive properties
            switch (id)
            {
                case 5:
                    drive.ReallocatedSectors = (long)rawVal;
                    break;
                case 9:
                    drive.PowerOnHours = (long)rawVal;
                    break;
                case 12:
                    drive.PowerCycles = (long)rawVal;
                    break;
                case 187:
                    drive.UncorrectableErrors = (long)rawVal;
                    break;
                case 194 or 190:
                    var tempC = (int)(rawVal & 0xFF);
                    if (tempC <= 0 || tempC >= 120) tempC = (int)(rawVal & 0xFFFF);
                    if (!drive.TemperatureC.HasValue && tempC is > 0 and < 120)
                    {
                        drive.TemperatureC = tempC;
                    }
                    break;
                case 197:
                    drive.PendingSectors = (long)rawVal;
                    break;
                case 198:
                    if (!drive.UncorrectableErrors.HasValue || drive.UncorrectableErrors == 0)
                        drive.UncorrectableErrors = (long)rawVal;
                    break;
                case 199:
                    drive.CrcErrors = (long)rawVal;
                    break;
                case 202 or 231 or 233:
                    if (!drive.LifeRemainingPercent.HasValue)
                    {
                        drive.LifeRemainingPercent = id == 202 ? Math.Max(0, 100 - (int)rawVal) : Math.Clamp(curr, 0, 100);
                    }
                    break;
                case 241:
                    drive.TotalBytesWritten = rawVal * 512UL;
                    break;
                case 242:
                    drive.TotalBytesRead = rawVal * 512UL;
                    break;
            }
        }
    }

    private static string FormatSmartRaw(int id, uint rawVal)
    {
        return id switch
        {
            9 => $"{rawVal:N0} hrs",
            12 => $"{rawVal:N0} cycles",
            194 or 190 => $"{rawVal & 0xFF}Â°C",
            202 => $"{rawVal}% used",
            231 => $"{rawVal}% left",
            241 or 242 => FormatBytes((long)rawVal * 512L),
            _ => rawVal.ToString("N0")
        };
    }

    private static void ParseAtaIdentify(PhysicalDriveInfo drive, byte[] identifyBytes)
    {
        if (identifyBytes == null || identifyBytes.Length < 512) return;

        // Model: words 27-46 (bytes 54-93), byte-swapped
        var model = SwapAtaString(identifyBytes, 54, 40);
        if (!string.IsNullOrWhiteSpace(model))
        {
            drive.Model = model;
        }

        // Serial: words 10-19 (bytes 20-39), byte-swapped
        var serial = SwapAtaString(identifyBytes, 20, 20);
        if (!string.IsNullOrWhiteSpace(serial))
        {
            drive.SerialNumber = serial;
        }

        // Nominal Media Rotation Rate: word 217 (bytes 434-435)
        var rotationRate = BitConverter.ToUInt16(identifyBytes, 434);
        if (rotationRate == 1)
        {
            drive.MediaType = drive.BusType == "USB" ? "USB (SATA SSD)" : "SATA SSD";
        }
        else if (rotationRate > 1)
        {
            drive.MediaType = drive.BusType == "USB" ? $"USB ({rotationRate} RPM HDD)" : $"{rotationRate} RPM HDD";
        }
    }

    private static string SwapAtaString(byte[] buffer, int offset, int length)
    {
        if (offset + length > buffer.Length) return string.Empty;
        var chars = new char[length];
        for (int i = 0; i < length; i += 2)
        {
            chars[i] = (char)buffer[offset + i + 1];
            chars[i + 1] = (char)buffer[offset + i];
        }
        return new string(chars).Trim();
    }



    private static List<StorageEvent> QueryRecentStorageEvents(int diskIndex)
    {
        var list = new List<StorageEvent>();
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            var cutoffText = cutoff.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
            var xpath = $"*[System[TimeCreated[@SystemTime >= '{cutoffText}'] and (Level=1 or Level=2 or Level=3) and (Provider[@Name='disk' or @Name='stornvme' or @Name='storahci' or @Name='volmgr' or @Name='ntfs'])]]";
            var query = new EventLogQuery("System", PathType.LogName, xpath);
            using var reader = new EventLogReader(query);

            EventRecord? record;
            while ((record = reader.ReadEvent()) != null && list.Count < 30)
            {
                using (record)
                {
                    var created = record.TimeCreated;
                    if (!created.HasValue || created.Value < cutoff || created.Value > DateTime.Now.AddMinutes(5))
                        continue;

                    var msg = record.FormatDescription() ?? "";

                    // Match disk number from message
                    var match = System.Text.RegularExpressions.Regex.Match(
                        msg,
                        @"(?:Disk|Harddisk|DR)\s*(\d+)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    if (match.Success && int.TryParse(match.Groups[1].Value, out var eventDiskIndex))
                    {
                        if (eventDiskIndex != diskIndex) continue;
                    }
                    else
                    {
                        // Without a disk identifier, this event cannot be safely attributed to this drive.
                        continue;
                    }

                    list.Add(new StorageEvent
                    {
                        Time = record.TimeCreated?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                        Source = record.ProviderName ?? "System",
                        Level = record.Level switch { 1 => "Critical", 2 => "Error", 3 => "Warning", _ => "Info" },
                        Id = record.Id.ToString(),
                        Message = msg.Length > 250 ? msg[..250] + "â€¦" : msg
                    });
                }
            }
        }
        catch
        {
            // Ignore event log query errors
        }
        return list;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}

