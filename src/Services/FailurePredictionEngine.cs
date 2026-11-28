using System;
using System.Collections.Generic;

using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

/// <summary>
/// Intelligent drive failure prediction engine.
/// Evaluates NVMe health telemetry, ATA SMART attributes, historical wear rates,
/// and Windows storage event signals to provide advance notice (at least 30 days)
/// before imminent drive failure.
/// </summary>
public static class FailurePredictionEngine
{
    public static DriveFailurePrediction Evaluate(PhysicalDriveInfo drive, HistoricalTrend? trend)
    {
        var prediction = new DriveFailurePrediction();

        // 0. Virtual disks have no independent physical-media SMART health.
        if (drive.IsVirtualDisk)
        {
            prediction.Severity = FailureSeverity.None;
            prediction.Title = "Healthy (Virtual Disk)";
            prediction.Summary = "Virtual storage device operating normally.";
            prediction.RecommendedAction = "None required.";
            prediction.RiskFactors = new List<string>();
            prediction.EstimatedDaysToFailure = null;
            return prediction;
        }

        var risks = new List<string>();
        var severity = FailureSeverity.None;
        int? estimatedDays = null;

        // 1. NVMe Spare Block Exhaustion (Crucial for 30-day advance notice on NVMe)
        if (drive.AvailableSparePercent.HasValue)
        {
            var spare = drive.AvailableSparePercent.Value;
            if (spare <= 10)
            {
                severity = Max(severity, FailureSeverity.CriticalImmediate);
                risks.Add($"Available spare flash blocks critically depleted ({spare}%). Drive lockdown or catastrophic failure is imminent.");
                estimatedDays = MinDays(estimatedDays, 7);
            }
            else if (spare <= 20)
            {
                severity = Max(severity, FailureSeverity.Warning30Days);
                risks.Add($"Available spare flash blocks have fallen to {spare}%. NAND block degradation is accelerating. Replace drive promptly.");
                estimatedDays = MinDays(estimatedDays, 30);
            }
            else if (spare < 80)
            {
                severity = Max(severity, FailureSeverity.AttentionNeeded);
                risks.Add($"Spare flash blocks have started declining ({spare}% remaining). Monitor closely.");
                estimatedDays = MinDays(estimatedDays, 60);
            }
        }

        // 2. NVMe Media and Data Integrity Errors
        if (drive.MediaErrors.HasValue && drive.MediaErrors.Value > 0)
        {
            var count = drive.MediaErrors.Value;
            if (count >= 3 || (trend != null && trend.MediaErrorsDelta > 0))
            {
                severity = Max(severity, FailureSeverity.Warning30Days);
                risks.Add($"Unrecoverable NAND media integrity errors detected ({count:N0} errors). ECC parity cannot compensate; backup data and replace drive.");
                estimatedDays = MinDays(estimatedDays, 30);
            }
            else
            {
                severity = Max(severity, FailureSeverity.AttentionNeeded);
                risks.Add($"Media integrity errors reported ({count:N0}). Flash cells are degrading.");
                estimatedDays = MinDays(estimatedDays, 60);
            }
        }

        // 3. SATA Reallocated Bad Sectors (SMART ID 5)
        if (drive.ReallocatedSectors.HasValue && drive.ReallocatedSectors.Value > 0)
        {
            var count = drive.ReallocatedSectors.Value;
            if (trend != null && trend.ReallocatedSectorsDelta > 0)
            {
                severity = Max(severity, FailureSeverity.Warning30Days);
                risks.Add($"Bad sectors are actively spreading ({count:N0} reallocated, +{trend.ReallocatedSectorsDelta} recently). Surface decay in progress. Replace drive promptly.");
                estimatedDays = MinDays(estimatedDays, 30);
            }
            else if (count >= 50)
            {
                severity = Max(severity, FailureSeverity.Warning30Days);
                risks.Add($"High count of reallocated bad sectors ({count:N0}). Drive reliability is compromised; backup data and replace drive.");
                estimatedDays = MinDays(estimatedDays, 45);
            }
            else
            {
                severity = Max(severity, FailureSeverity.AttentionNeeded);
                risks.Add($"Drive has {count:N0} reallocated sector(s). Surface health is degraded.");
                estimatedDays = MinDays(estimatedDays, 90);
            }
        }

        // 4. Current Pending Unstable Sectors (SMART ID 197 / 0xC5) - Strong immediate warning
        if (drive.PendingSectors.HasValue && drive.PendingSectors.Value > 0)
        {
            var count = drive.PendingSectors.Value;
            severity = Max(severity, FailureSeverity.Warning30Days);
            risks.Add($"Current pending sectors: {count:N0}. Sectors are unreadable and waiting to be reallocated. Active read failures are occurring; replace drive promptly.");
            estimatedDays = MinDays(estimatedDays, 30);
        }

        // 5. Offline Uncorrectable / Reported Uncorrectable Errors (SMART ID 187, 198)
        if (drive.UncorrectableErrors.HasValue && drive.UncorrectableErrors.Value > 0)
        {
            var count = drive.UncorrectableErrors.Value;
            severity = Max(severity, FailureSeverity.Warning30Days);
            risks.Add($"Reported uncorrectable read errors ({count:N0}). Drive controller failed to correct data with ECC. Replace drive promptly.");
            estimatedDays = MinDays(estimatedDays, 30);
        }

        // 6. SSD Endurance / Wear Level Lifespan Projection
        if (trend != null && trend.ProjectedDaysUntilEnduranceExhausted.HasValue)
        {
            var days = trend.ProjectedDaysUntilEnduranceExhausted.Value;
            if (days <= 30)
            {
                severity = Max(severity, FailureSeverity.Warning30Days);
                risks.Add($"At current write burn rate, SSD write endurance will be exhausted in ~{days} days. Replace drive.");
                estimatedDays = MinDays(estimatedDays, days);
            }
            else if (days <= 60)
            {
                severity = Max(severity, FailureSeverity.AttentionNeeded);
                risks.Add($"Projected write endurance depletion in ~{days} days. Plan drive replacement.");
                estimatedDays = MinDays(estimatedDays, days);
            }
        }
        else if (drive.LifeRemainingPercent.HasValue)
        {
            var life = drive.LifeRemainingPercent.Value;
            if (life <= 3)
            {
                severity = Max(severity, FailureSeverity.CriticalImmediate);
                risks.Add($"NAND endurance exhausted (only {life}% life remaining). Controller will switch to read-only soon.");
                estimatedDays = MinDays(estimatedDays, 14);
            }
            else if (life <= 10)
            {
                severity = Max(severity, FailureSeverity.Warning30Days);
                risks.Add($"Low remaining endurance ({life}% remaining). Drive should be replaced promptly.");
                estimatedDays = MinDays(estimatedDays, 30);
            }
            else if (life <= 20)
            {
                severity = Max(severity, FailureSeverity.AttentionNeeded);
                risks.Add($"Remaining endurance is getting low ({life}% remaining).");
            }
        }

        // 7. Thermal Warning
        if (drive.TemperatureC.HasValue)
        {
            var temp = drive.TemperatureC.Value;
            var maxSafe = drive.MediaType.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ? 75 : 55;
            if (temp >= maxSafe + 10)
            {
                risks.Add($"Dangerous operating temperature: {temp}Â°C (safe ceiling is {maxSafe}Â°C). Extreme heat accelerates drive failure.");
                if (severity < FailureSeverity.AttentionNeeded) severity = FailureSeverity.AttentionNeeded;
            }
        }

        // 8. Correlated Windows Event Log Errors
        // Differentiate definitive hardware fault events (bad blocks, resets, paging failures)
        // from isolated/transient Storport miniport retries on external USB or virtual-backed drives.
        var badBlockEvents = 0;       // Event 7: Bad block
        var controllerErrors = 0;     // Event 11: Controller error
        var pagingErrors = 0;         // Event 51: Paging operation error
        var deviceResets = 0;         // Event 129: Device reset
        var retryEvents = new List<DateTime>(); // Event 153: Storport I/O retry

        var eventCutoff = DateTime.Now.AddDays(-30);
        foreach (var ev in drive.StorageEvents)
        {
            // Re-check the time window so stale or malformed history cannot inflate risk.
            if (!DateTime.TryParse(ev.Time, out var eventTime) ||
                eventTime < eventCutoff ||
                eventTime > DateTime.Now.AddMinutes(5))
            {
                continue;
            }

            switch (ev.Id)
            {
                case "7":
                    badBlockEvents++;
                    break;
                case "11":
                    controllerErrors++;
                    break;
                case "51":
                    pagingErrors++;
                    break;
                case "129":
                    deviceResets++;
                    break;
                case "153":
                    retryEvents.Add(eventTime);
                    break;
            }
        }

        // 8a. Physical Bad Blocks (Event 7) - Active media degradation
        if (badBlockEvents > 0)
        {
            severity = Max(severity, FailureSeverity.Warning30Days);
            risks.Add($"Windows logged {badBlockEvents} physical bad block error(s) (Event ID 7) in recent event logs.");
            estimatedDays = MinDays(estimatedDays, 30);
        }

        // 8b. Paging / Controller Errors (Events 11 / 51) - Critical subsystem failures
        if (pagingErrors > 0)
        {
            severity = Max(severity, FailureSeverity.Warning30Days);
            risks.Add($"Windows logged {pagingErrors} paging I/O error(s) (Event ID 51). Virtual memory page faults on physical storage.");
            estimatedDays = MinDays(estimatedDays, 30);
        }
        if (controllerErrors > 0)
        {
            severity = Max(severity, FailureSeverity.Warning30Days);
            risks.Add($"Windows logged {controllerErrors} storage controller error(s) (Event ID 11). Hardware or cable communication failure.");
            estimatedDays = MinDays(estimatedDays, 30);
        }

        // 8c. Hardware Device Resets (Event 129) - Storage miniport issued hardware reset
        if (deviceResets >= 3)
        {
            severity = Max(severity, FailureSeverity.Warning30Days);
            risks.Add($"Windows logged {deviceResets} hardware device resets (Event ID 129) in recent event logs. Controller is hanging under I/O load.");
            estimatedDays = MinDays(estimatedDays, 30);
        }
        else if (deviceResets >= 1)
        {
            severity = Max(severity, FailureSeverity.AttentionNeeded);
            risks.Add($"Windows logged {deviceResets} hardware device reset(s) (Event ID 129). Monitor cable and controller connection.");
            estimatedDays = MinDays(estimatedDays, 60);
        }

        // 8d. Storport I/O Retries (Event 153)
        // Storport logs Event 153 when an I/O request hits a timeout and is retried.
        // On external USB drives (especially those hosting large VHDX files or subject to USB sleep/LPM),
        // isolated retries (e.g. 1-4 occurrences over 30 days, spaced days or weeks apart) with 0 bad blocks
        // and 0 resets are completely benign transient delays (drive spin-up or heavy container flush).
        // Only burst retries (>= 5 within 24 hours) or high aggregate volume (>= 15) indicate real hardware degradation.
        if (retryEvents.Count > 0)
        {
            var isBurst = HasRetryBurst(retryEvents, clusterThreshold: 5, windowHours: 24);
            var isHighVolume = retryEvents.Count >= 15;

            if (isBurst || isHighVolume)
            {
                severity = Max(severity, FailureSeverity.AttentionNeeded);
                risks.Add($"Frequent I/O retries detected ({retryEvents.Count} Event 153 events). Storage interface is experiencing recurring latency stalls.");
                estimatedDays = MinDays(estimatedDays, 60);
            }
            else if (drive.ReallocatedSectors > 0 || drive.PendingSectors > 0 || deviceResets > 0)
            {
                // Surface or controller issues already present; mention correlated retries
                risks.Add($"{retryEvents.Count} I/O retry event(s) logged (Event ID 153) coinciding with disk surface/controller warnings.");
            }
            else
            {
                // Benign isolated retries. Do NOT trigger a failure warning or dock health score.
                // Informational only.
            }
        }

        // Set outcome
        prediction.Severity = severity;
        prediction.RiskFactors = risks;
        prediction.EstimatedDaysToFailure = estimatedDays;

        switch (severity)
        {
            case FailureSeverity.CriticalImmediate:
                prediction.Title = "CRITICAL â€” REPLACE IMMEDIATELY";
                prediction.Summary = "Critical hardware degradation or impending controller lockdown detected. Back up data immediately and replace the drive now.";
                prediction.RecommendedAction = "URGENT: Back up all vital files right now. Do not perform stress tests or large writes. Replace drive immediately.";
                break;

            case FailureSeverity.Warning30Days:
                prediction.Title = "FAILING";
                prediction.Summary = estimatedDays.HasValue
                    ? $"Hardware telemetry indicates active degradation (estimated ~{estimatedDays.Value} days remaining)."
                    : "Hardware telemetry indicates active degradation. Replacement recommended.";
                prediction.RecommendedAction = "Back up important files and order a replacement drive. This drive is actively degrading.";
                break;

            case FailureSeverity.AttentionNeeded:
                prediction.Title = "ATTENTION NEEDED";
                prediction.Summary = "Early wear or minor surface degradation detected. The drive is currently functional but showing early warning signs.";
                prediction.RecommendedAction = "Ensure daily backups are running and monitor bad sector/wear trends weekly.";
                break;

            default:
                if (!drive.HasHardwareHealthTelemetry)
                {
                    prediction.Title = "Telemetry Limited";
                    if (drive.BusType == "USB")
                    {
                        prediction.Summary = "USB enclosure bridge chip did not pass through SCSI-to-ATA Translation (SAT) commands. The bridge firmware dropped or lacks native pass-through capability.";
                        prediction.RecommendedAction = "To monitor SMART on this drive, use an enclosure with a SAT/UASP bridge chip (e.g., Realtek RTL9210, ASMedia ASM235CM) or connect directly to an internal SATA/NVMe port.";
                    }
                    else
                    {
                        prediction.Summary = "Hardware SMART telemetry is unavailable for this storage device.";
                        prediction.RecommendedAction = "Verify controller/driver SMART support.";
                    }
                }
                else
                {
                    prediction.Title = "Healthy";
                    prediction.Summary = "Hardware telemetry reports normal operation. No failure indicators detected.";
                    prediction.RecommendedAction = "None required.";
                }
                break;
        }

        return prediction;
    }

    public static int CalculateHealthScore(PhysicalDriveInfo drive, DriveFailurePrediction prediction)
    {
        if (drive.IsVirtualDisk) return 100;
        if (!drive.HasHardwareHealthTelemetry) return 0;

        var score = 100;

        // Deduction based on failure severity
        switch (prediction.Severity)
        {
            case FailureSeverity.CriticalImmediate:
                score = Math.Min(score, 25);
                break;
            case FailureSeverity.Warning30Days:
                score = Math.Min(score, 50);
                break;
            case FailureSeverity.AttentionNeeded:
                score = Math.Min(score, 75);
                break;
        }

        // Deduction for LifeRemainingPercent
        if (drive.LifeRemainingPercent.HasValue)
        {
            var wearScore = drive.LifeRemainingPercent.Value;
            score = Math.Min(score, Math.Max(20, wearScore));
        }

        // Deduction for AvailableSparePercent
        if (drive.AvailableSparePercent.HasValue)
        {
            var spare = drive.AvailableSparePercent.Value;
            if (spare < 100)
            {
                score -= (100 - spare) / 2;
            }
        }

        // Deduction for bad sectors
        if (drive.ReallocatedSectors is > 0)
        {
            score -= (int)Math.Min(40, drive.ReallocatedSectors.Value * 2);
        }
        if (drive.PendingSectors is > 0)
        {
            score -= (int)Math.Min(40, drive.PendingSectors.Value * 5);
        }
        if (drive.MediaErrors is > 0)
        {
            score -= (int)Math.Min(50, drive.MediaErrors.Value * 10);
        }
        if (drive.UncorrectableErrors is > 0)
        {
            score -= (int)Math.Min(50, drive.UncorrectableErrors.Value * 10);
        }

        return Math.Clamp(score, 0, 100);
    }

    private static FailureSeverity Max(FailureSeverity a, FailureSeverity b) => (int)a > (int)b ? a : b;

    private static int? MinDays(int? current, int? candidate)
    {
        if (!current.HasValue) return candidate;
        if (!candidate.HasValue) return current;
        return Math.Min(current.Value, candidate.Value);
    }

    private static bool HasRetryBurst(List<DateTime> retries, int clusterThreshold, double windowHours)
    {
        if (retries.Count < clusterThreshold) return false;
        var validDates = new List<DateTime>();
        foreach (var r in retries)
        {
            if (r != DateTime.MinValue) validDates.Add(r);
        }
        if (validDates.Count < clusterThreshold) return false;
        validDates.Sort();

        for (var i = 0; i <= validDates.Count - clusterThreshold; i++)
        {
            var span = validDates[i + clusterThreshold - 1] - validDates[i];
            if (span.TotalHours <= windowHours) return true;
        }
        return false;
    }
}


