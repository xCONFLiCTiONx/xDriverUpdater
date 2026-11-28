using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

public sealed class PowerCfgService
{
    private static readonly Regex SchemeLineRegex = new(
        @"Power Scheme GUID:\s*([0-9a-fA-F\-]{36})\s+\((.+?)\)(\s*\*)?\r?$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex SettingGuidRegex = new(
        @"Power Setting GUID:\s*([0-9a-fA-F\-]{36})",
        RegexOptions.Compiled);

    private static readonly Regex AcValueRegex = new(
        @"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)",
        RegexOptions.Compiled);

    public (int ExitCode, string Output) RunPowerCfg(string arguments, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start powercfg.exe");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(); } catch { }
            return (-1, "Timed out waiting for powercfg.exe");
        }

        var combined = stdoutTask.Result;
        var err = stderrTask.Result;
        if (!string.IsNullOrWhiteSpace(err))
            combined = string.IsNullOrWhiteSpace(combined) ? err : $"{combined}\n{err}";

        return (proc.ExitCode, combined);
    }

    public List<PowerSchemeInfo> ListSchemes()
    {
        var result = new List<PowerSchemeInfo>();
        var (code, output) = RunPowerCfg("/list");
        if (code != 0) return result;

        foreach (Match m in SchemeLineRegex.Matches(output))
        {
            if (!Guid.TryParse(m.Groups[1].Value, out var guid)) continue;
            result.Add(new PowerSchemeInfo
            {
                Guid = guid,
                Name = m.Groups[2].Value.Trim(),
                IsActive = !string.IsNullOrWhiteSpace(m.Groups[3].Value)
            });
        }
        return result;
    }

    /// <summary>
    /// Reads all AC setting values (including hidden settings via /qh) for a scheme.
    /// Returns SettingGuid -> AC uint value.
    /// </summary>
    public Dictionary<Guid, uint> QueryAllAcValues(Guid schemeGuid)
    {
        var map = new Dictionary<Guid, uint>();
        var (code, output) = RunPowerCfg($"/qh {schemeGuid}");
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return map;

        Guid? currentSetting = null;
        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var mSetting = SettingGuidRegex.Match(line);
            if (mSetting.Success && Guid.TryParse(mSetting.Groups[1].Value, out var sg))
            {
                currentSetting = sg;
                continue;
            }

            if (currentSetting.HasValue)
            {
                var mAc = AcValueRegex.Match(line);
                if (mAc.Success && uint.TryParse(mAc.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var val))
                {
                    map[currentSetting.Value] = val;
                    currentSetting = null;
                }
            }
        }

        return map;
    }

    public (bool Success, Guid NewGuid, string Output) DuplicateScheme(Guid baseSchemeGuid)
    {
        var (code, output) = RunPowerCfg($"/duplicatescheme {baseSchemeGuid}");
        if (code != 0) return (false, Guid.Empty, output);

        var m = SchemeLineRegex.Match(output);
        if (m.Success && Guid.TryParse(m.Groups[1].Value, out var newGuid))
            return (true, newGuid, output);

        var anyGuid = Regex.Match(output, @"([0-9a-fA-F]{8}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{12})");
        if (anyGuid.Success && Guid.TryParse(anyGuid.Groups[1].Value, out var parsed))
            return (true, parsed, output);

        return (false, Guid.Empty, output);
    }

    public (bool Success, string Output) RenameScheme(Guid schemeGuid, string name, string description)
    {
        var safeName = name.Replace("\"", "'");
        var safeDesc = description.Replace("\"", "'");
        var (code, output) = RunPowerCfg($"/changename {schemeGuid} \"{safeName}\" \"{safeDesc}\"");
        return (code == 0, output);
    }

    public (bool Success, string Output) SetSettingValue(Guid schemeGuid, Guid subgroupGuid, Guid settingGuid, uint value, bool applyToDcToo = true)
    {
        var (acCode, acOut) = RunPowerCfg($"/setacvalueindex {schemeGuid} {subgroupGuid} {settingGuid} {value}");
        if (acCode != 0) return (false, acOut);

        if (applyToDcToo)
        {
            RunPowerCfg($"/setdcvalueindex {schemeGuid} {subgroupGuid} {settingGuid} {value}");
        }
        return (true, acOut);
    }

    public (bool Success, string Output) SetActiveScheme(Guid schemeGuid)
    {
        var (code, output) = RunPowerCfg($"/setactive {schemeGuid}");
        return (code == 0, output);
    }

    public (bool Success, string Output) ExportScheme(Guid schemeGuid, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        var (code, output) = RunPowerCfg($"/export \"{filePath}\" {schemeGuid}");
        return (code == 0, output);
    }

    public (bool Success, string Output) DeleteScheme(Guid schemeGuid)
    {
        var (code, output) = RunPowerCfg($"/delete {schemeGuid}");
        return (code == 0, output);
    }

    public (bool Success, string Output) UnhideSetting(Guid subgroupGuid, Guid settingGuid)
    {
        var (code, output) = RunPowerCfg($"-attributes {subgroupGuid} {settingGuid} -ATTRIB_HIDE");
        return (code == 0, output);
    }
}
