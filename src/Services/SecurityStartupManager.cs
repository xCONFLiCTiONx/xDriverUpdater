using System.Diagnostics;
using System.IO;

namespace xDriverUpdater.Services;

public static class SecurityStartupManager
{
    public const string TaskName = "xSecurityCenter";
    public const string SilentArgument = "--silent";

    public static string GetExecutablePath()
    {
        var exeInDir = Path.Combine(AppContext.BaseDirectory, "xSecurityCenter.exe");
        if (File.Exists(exeInDir)) return exeInDir;

        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path) &&
            !path.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(path))
            return path;

        return exeInDir;
    }

    public static bool IsStartupEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/query /tn \"{TaskName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return proc.ExitCode == 0 && output.Contains(TaskName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool SetStartup(bool enable)
    {
        try
        {
            if (enable)
            {
                var exePath = GetExecutablePath();
                var cmd = $"/create /tn \"{TaskName}\" /tr \"\\\"{exePath}\\\" {SilentArgument}\" /sc onlogon /rl highest /f";
                var psi = new ProcessStartInfo("schtasks.exe", cmd)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
                return IsStartupEnabled();
            }
            else
            {
                var psi = new ProcessStartInfo("schtasks.exe", $"/delete /tn \"{TaskName}\" /f")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
                return !IsStartupEnabled();
            }
        }
        catch { return false; }
    }
}
