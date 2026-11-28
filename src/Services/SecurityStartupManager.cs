using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;

namespace xDriverUpdater.Services;

public static class SecurityStartupManager
{
    public const string TaskName = "xSecurityCenter";
    public const string SilentArgument = "--silent";

    public static string GetExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path) &&
            !path.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(path))
            return path;

        var exeInDir = Path.Combine(AppContext.BaseDirectory, "xSecurityCenter.exe");
        return exeInDir;
    }

    public static bool IsStartupEnabled()
    {
        try
        {
            var (code, output) = RunSchtasks($"/query /tn \"{TaskName}\"");
            return code == 0 && output.Contains(TaskName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static (bool Success, string Message) SetStartup(bool enable)
    {
        try
        {
            if (enable)
            {
                var exePath = GetExecutablePath();
                var userId = WindowsIdentity.GetCurrent().Name;

                var xml = $"""
                    <?xml version="1.0" encoding="UTF-16"?>
                    <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                      <RegistrationInfo>
                        <Description>Starts xSecurityCenter elevated in the system tray at user logon.</Description>
                      </RegistrationInfo>
                      <Triggers>
                        <LogonTrigger>
                          <Enabled>true</Enabled>
                          <UserId>{SecurityElement.Escape(userId)}</UserId>
                        </LogonTrigger>
                      </Triggers>
                      <Principals>
                        <Principal id="Author">
                          <UserId>{SecurityElement.Escape(userId)}</UserId>
                          <LogonType>InteractiveToken</LogonType>
                          <RunLevel>HighestAvailable</RunLevel>
                        </Principal>
                      </Principals>
                      <Settings>
                        <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                        <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                        <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                        <AllowHardTerminate>false</AllowHardTerminate>
                        <StartWhenAvailable>true</StartWhenAvailable>
                        <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                        <AllowStartOnDemand>true</AllowStartOnDemand>
                        <Enabled>true</Enabled>
                        <Hidden>false</Hidden>
                        <RunOnlyIfIdle>false</RunOnlyIfIdle>
                        <WakeToRun>false</WakeToRun>
                        <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                        <Priority>7</Priority>
                      </Settings>
                      <Actions Context="Author">
                        <Exec>
                          <Command>{SecurityElement.Escape(exePath)}</Command>
                          <Arguments>{SilentArgument}</Arguments>
                          <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory)}</WorkingDirectory>
                        </Exec>
                      </Actions>
                    </Task>
                    """;

                var tempXml = Path.Combine(Path.GetTempPath(), $"xSecurityCenter_logon_{Guid.NewGuid():N}.xml");
                try
                {
                    File.WriteAllText(tempXml, xml, System.Text.Encoding.Unicode);
                    var (code, output) = RunSchtasks($"/create /tn \"{TaskName}\" /xml \"{tempXml}\" /f");
                    if (code == 0 && IsStartupEnabled())
                        return (true, $"Registered elevated logon startup task '{TaskName}'.");
                    return (false, string.IsNullOrWhiteSpace(output) ? $"schtasks exited with code {code}." : output.Trim());
                }
                finally
                {
                    try { if (File.Exists(tempXml)) File.Delete(tempXml); } catch { }
                }
            }
            else
            {
                var (code, output) = RunSchtasks($"/delete /tn \"{TaskName}\" /f");
                if (!IsStartupEnabled())
                    return (true, $"Removed logon startup task '{TaskName}'.");
                return (false, string.IsNullOrWhiteSpace(output) ? $"schtasks exited with code {code}." : output.Trim());
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (int ExitCode, string Output) RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return (-1, "Failed to start schtasks.exe");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(8000);
        var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}".Trim();
        return (proc.ExitCode, combined);
    }
}
