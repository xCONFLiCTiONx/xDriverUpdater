using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace xDriverUpdater.Services;

public sealed class RegistryBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string BackupDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "xPrograms", "xSecurityCenter", "Backups", "ContextGuard");

    public string LegacyBackupDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "xContextGuard", "Backups");

    public string BackupKey(string registryPath, string reason, string? companionRegistryPath = null)
    {
        var (hive, subPath) = ParsePath(registryPath);
        using var root = OpenHive(hive);
        using var key = root.OpenSubKey(subPath, writable: false)
            ?? throw new InvalidOperationException($"Registry key no longer exists: {registryPath}");

        var snapshot = Capture(registryPath, key);
        if (!string.IsNullOrWhiteSpace(companionRegistryPath))
        {
            snapshot.CompanionKey = TryCaptureOptionalKey(companionRegistryPath);
        }

        Directory.CreateDirectory(BackupDirectory);
        var safeName = string.Concat(reason.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        var file = Path.Combine(BackupDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{safeName}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(snapshot, JsonOptions));
        return file;
    }

    public string BackupFile(string targetFilePath, string reason, string? companionRegistryPath = null)
    {
        if (!File.Exists(targetFilePath))
            throw new FileNotFoundException($"File to back up does not exist: {targetFilePath}");

        var snapshot = new FileSnapshot
        {
            Type = "File",
            FilePath = targetFilePath,
            ContentBase64 = Convert.ToBase64String(File.ReadAllBytes(targetFilePath)),
            CompanionKey = !string.IsNullOrWhiteSpace(companionRegistryPath)
                ? TryCaptureOptionalKey(companionRegistryPath)
                : null
        };

        Directory.CreateDirectory(BackupDirectory);
        var safeName = string.Concat(reason.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        var file = Path.Combine(BackupDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{safeName}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(snapshot, JsonOptions));
        return file;
    }

    private static RegistryKeySnapshot? TryCaptureOptionalKey(string registryPath)
    {
        try
        {
            var (hive, subPath) = ParsePath(registryPath);
            using var root = OpenHive(hive);
            using var key = root.OpenSubKey(subPath, writable: false);
            return key is not null ? Capture(registryPath, key) : null;
        }
        catch
        {
            return null;
        }
    }

    public void RestoreBackup(string filePath)
    {
        var text = File.ReadAllText(filePath);
        using var doc = JsonDocument.Parse(text);
        var rootElement = doc.RootElement;

        if ((rootElement.TryGetProperty("Type", out var typeProp) && typeProp.GetString() == "File") ||
            (rootElement.TryGetProperty("FilePath", out _) && !rootElement.TryGetProperty("RegistryPath", out _)))
        {
            var fileSnapshot = JsonSerializer.Deserialize<FileSnapshot>(text)
                ?? throw new InvalidDataException("The selected file backup is empty or invalid.");

            var dir = Path.GetDirectoryName(fileSnapshot.FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllBytes(fileSnapshot.FilePath, Convert.FromBase64String(fileSnapshot.ContentBase64));

            if (fileSnapshot.CompanionKey is not null)
            {
                RestoreKeySnapshot(fileSnapshot.CompanionKey);
            }
            return;
        }

        var snapshot = JsonSerializer.Deserialize<RegistryKeySnapshot>(text)
            ?? throw new InvalidDataException("The selected backup is empty or invalid.");
        RestoreKeySnapshot(snapshot);
        if (snapshot.CompanionKey is not null)
        {
            RestoreKeySnapshot(snapshot.CompanionKey);
        }
    }

    private static void RestoreKeySnapshot(RegistryKeySnapshot snapshot)
    {
        var (hive, subPath) = ParsePath(snapshot.RegistryPath);
        using var root = OpenHive(hive);
        using var key = root.CreateSubKey(subPath, writable: true)
            ?? throw new InvalidOperationException($"Could not recreate registry key: {snapshot.RegistryPath}");
        RestoreInto(key, snapshot);
    }

    private static RegistryKeySnapshot Capture(string path, RegistryKey key)
    {
        var result = new RegistryKeySnapshot { RegistryPath = path };
        foreach (var valueName in key.GetValueNames())
        {
            var kind = key.GetValueKind(valueName);
            var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            result.Values.Add(new RegistryValueSnapshot
            {
                Name = valueName,
                Kind = kind.ToString(),
                Value = EncodeValue(value, kind)
            });
        }

        foreach (var childName in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(childName, writable: false);
            if (child is not null)
                result.Children.Add(Capture(path + "\\" + childName, child));
        }
        return result;
    }

    private static void RestoreInto(RegistryKey key, RegistryKeySnapshot snapshot)
    {
        foreach (var valueName in key.GetValueNames())
            key.DeleteValue(valueName, throwOnMissingValue: false);
        foreach (var childName in key.GetSubKeyNames())
            key.DeleteSubKeyTree(childName, throwOnMissingSubKey: false);

        foreach (var item in snapshot.Values)
        {
            if (!Enum.TryParse<RegistryValueKind>(item.Kind, out var kind))
                continue;
            key.SetValue(item.Name, DecodeValue(item.Value, kind), kind);
        }

        foreach (var childSnapshot in snapshot.Children)
        {
            var childName = childSnapshot.RegistryPath[(childSnapshot.RegistryPath.LastIndexOf('\\') + 1)..];
            using var child = key.CreateSubKey(childName, writable: true)
                ?? throw new InvalidOperationException($"Could not restore registry subkey: {childName}");
            RestoreInto(child, childSnapshot);
        }
    }

    private static object EncodeValue(object? value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.Binary when value is byte[] bytes => Convert.ToBase64String(bytes),
        RegistryValueKind.MultiString when value is string[] strings => strings,
        RegistryValueKind.DWord when value is int number => number,
        RegistryValueKind.QWord when value is long number => number,
        _ => value?.ToString() ?? string.Empty
    };

    private static object DecodeValue(object value, RegistryValueKind kind)
    {
        var element = value is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(value);

        return kind switch
        {
            RegistryValueKind.Binary => Convert.FromBase64String(element.GetString() ?? ""),
            RegistryValueKind.MultiString => element.Deserialize<string[]>() ?? [],
            RegistryValueKind.DWord => element.GetInt32(),
            RegistryValueKind.QWord => element.GetInt64(),
            RegistryValueKind.None => Array.Empty<byte>(),
            _ => element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : element.ToString()
        };
    }

    internal static (RegistryHive Hive, string SubPath) ParsePath(string path)
    {
        var split = path.IndexOf('\\');
        if (split < 0)
            throw new ArgumentException("Registry path must include a hive and subkey.", nameof(path));
        var hiveName = path[..split].ToUpperInvariant();
        var hive = hiveName switch
        {
            "HKEY_CLASSES_ROOT" or "HKCR" => RegistryHive.ClassesRoot,
            "HKEY_CURRENT_USER" or "HKCU" => RegistryHive.CurrentUser,
            "HKEY_LOCAL_MACHINE" or "HKLM" => RegistryHive.LocalMachine,
            _ => throw new ArgumentException($"Unsupported registry hive: {hiveName}")
        };
        return (hive, path[(split + 1)..]);
    }

    private static RegistryKey OpenHive(RegistryHive hive) =>
        RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

    private sealed class RegistryKeySnapshot
    {
        public string RegistryPath { get; set; } = "";
        public List<RegistryValueSnapshot> Values { get; set; } = [];
        public List<RegistryKeySnapshot> Children { get; set; } = [];
        public RegistryKeySnapshot? CompanionKey { get; set; }
    }

    private sealed class RegistryValueSnapshot
    {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public object Value { get; set; } = "";
    }

    private sealed class FileSnapshot
    {
        public string Type { get; set; } = "File";
        public string FilePath { get; set; } = "";
        public string ContentBase64 { get; set; } = "";
        public RegistryKeySnapshot? CompanionKey { get; set; }
    }
}
