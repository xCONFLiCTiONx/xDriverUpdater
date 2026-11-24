using System.Text.RegularExpressions;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

public static class VendorDatabase
{
    // PCI / HDAUDIO Silicon Vendor IDs (4-hex uppercase)
    private static readonly Dictionary<string, string> PciVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["10DE"] = "NVIDIA",
        ["1002"] = "AMD",
        ["1022"] = "AMD",
        ["8086"] = "Intel",
        ["10EC"] = "Realtek",
        ["17CB"] = "Qualcomm",
        ["168C"] = "Qualcomm Atheros",
        ["1969"] = "Qualcomm Atheros",
        ["14C3"] = "MediaTek",
        ["14E4"] = "Broadcom",
        ["1B21"] = "ASMedia",
        ["144D"] = "Samsung",
        ["15B7"] = "Western Digital",
        ["C0A9"] = "Crucial / Micron",
        ["1E4B"] = "Maxio",
        ["1987"] = "Phison",
        ["1CC1"] = "ADATA",
        ["1179"] = "Kioxia / Toshiba",
        ["1C5C"] = "SK Hynix",
        ["1D6A"] = "Aquantia / Marvell",
        ["1B4B"] = "Marvell",
        ["1000"] = "Broadcom / LSI",
        ["1025"] = "Acer",
        ["1028"] = "Dell",
        ["17AA"] = "Lenovo",
        ["103C"] = "HP",
        ["1043"] = "ASUS",
        ["1462"] = "MSI",
        ["1458"] = "Gigabyte",
        ["1849"] = "ASRock",
        ["1A58"] = "Razer",
        ["1414"] = "Microsoft",
        ["15AD"] = "VMware",
        ["1AF4"] = "Red Hat / VirtIO",
        ["1235"] = "Focusrite",
        ["1102"] = "Creative",
        ["14F1"] = "Conexant / Synaptics",
        ["1013"] = "Cirrus Logic",
        ["1D5C"] = "Fresco Logic",
        ["1912"] = "Renesas",
        ["1106"] = "VIA"
    };

    // USB Vendor IDs
    private static readonly Dictionary<string, string> UsbVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1235"] = "Focusrite",
        ["046D"] = "Logitech",
        ["1B1C"] = "Corsair",
        ["1532"] = "Razer",
        ["1038"] = "SteelSeries",
        ["0B05"] = "ASUS",
        ["045E"] = "Microsoft",
        ["8087"] = "Intel",
        ["0BDA"] = "Realtek",
        ["0CF3"] = "Qualcomm Atheros",
        ["0E8D"] = "MediaTek",
        ["0A5C"] = "Broadcom",
        ["048D"] = "ITE Tech",
        ["1462"] = "MSI",
        ["04E8"] = "Samsung",
        ["0781"] = "SanDisk",
        ["1058"] = "Western Digital",
        ["0BC2"] = "Seagate",
        ["0951"] = "Kingston / HyperX",
        ["2516"] = "Cooler Master",
        ["3434"] = "Keychron",
        ["1E7D"] = "ROCCAT",
        ["054C"] = "Sony",
        ["05AC"] = "Apple",
        ["18D1"] = "Google",
        ["04A9"] = "Canon",
        ["03F0"] = "HP",
        ["04F9"] = "Brother",
        ["04B8"] = "Epson",
        ["27C6"] = "Goodix",
        ["06CB"] = "Synaptics",
        ["04F3"] = "ELAN",
        ["0489"] = "Foxconn / Hon Hai",
        ["13D3"] = "IMC Networks"
    };

    // Subsystem Vendor IDs that strongly indicate an OEM-branded board/laptop customization
    private static readonly Dictionary<string, string> OemSubsystemVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1025"] = "Acer",
        ["1028"] = "Dell",
        ["17AA"] = "Lenovo",
        ["103C"] = "HP",
        ["1043"] = "ASUS",
        ["1462"] = "MSI",
        ["1458"] = "Gigabyte",
        ["1849"] = "ASRock",
        ["1A58"] = "Razer",
        ["1414"] = "Microsoft",
        ["144D"] = "Samsung",
        ["1179"] = "Toshiba / Dynabook",
        ["1854"] = "LG",
        ["1558"] = "Clevo",
        ["1D05"] = "TongFang",
        ["104D"] = "Sony",
        ["10CF"] = "Fujitsu",
        ["1071"] = "MiTAC",
        ["152D"] = "Quanta",
        ["17C0"] = "Wistron",
        ["F111"] = "Framework"
    };

    // 3-letter PNP / EDID Monitor Manufacturer IDs (MONITOR\XXX####)
    private static readonly Dictionary<string, string> MonitorEdidVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ACR"] = "Acer",
        ["ACI"] = "ASUS",
        ["AUS"] = "ASUS",
        ["AOC"] = "AOC",
        ["BNQ"] = "BenQ",
        ["DEL"] = "Dell",
        ["GBT"] = "Gigabyte",
        ["GSM"] = "LG",
        ["HWP"] = "HP",
        ["LEN"] = "Lenovo",
        ["MSI"] = "MSI",
        ["PHL"] = "Philips",
        ["SAM"] = "Samsung",
        ["VSC"] = "ViewSonic",
        ["SPT"] = "Sceptre",
        ["NEC"] = "NEC",
        ["EIZ"] = "EIZO",
        ["IVM"] = "iiyama",
        ["SNY"] = "Sony",
        ["SKG"] = "Skyworth / Google TV",
        ["TCL"] = "TCL",
        ["HEC"] = "Hisense",
        ["VIZ"] = "Vizio",
        ["CMN"] = "Chimei Innolux (Laptop Panel)",
        ["AUO"] = "AU Optronics (Laptop Panel)",
        ["BOE"] = "BOE (Laptop Panel)",
        ["LGD"] = "LG Display (Laptop Panel)",
        ["SHP"] = "Sharp (Laptop Panel)",
        ["SDC"] = "Samsung Display (Laptop Panel)"
    };

    public static string LookupMonitorEdidVendor(string edid3)
    {
        if (string.IsNullOrWhiteSpace(edid3)) return "";
        return MonitorEdidVendors.TryGetValue(edid3.Trim(), out var name) ? name : "";
    }

    public static bool IsTvOrBuiltinPanelMonitor(string edid3, string displayName)
    {
        if (edid3.ToUpperInvariant() is "SKG" or "TCL" or "HEC" or "VIZ" or "CMN" or "AUO" or "BOE" or "LGD" or "SHP" or "SDC")
            return true;
        if (displayName.Contains("TV", StringComparison.OrdinalIgnoreCase) ||
            displayName.Contains("Internal", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public static string LookupSiliconVendor(string busType, string vendorId, string fallbackManufacturer)
    {
        if (busType.Equals("MONITOR", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(vendorId))
        {
            var mv = LookupMonitorEdidVendor(vendorId);
            if (!string.IsNullOrEmpty(mv)) return mv;
        }
        if (!string.IsNullOrWhiteSpace(vendorId))
        {
            if (busType.Equals("USB", StringComparison.OrdinalIgnoreCase) && UsbVendors.TryGetValue(vendorId, out var uv))
                return uv;
            if (PciVendors.TryGetValue(vendorId, out var pv))
                return pv;
            if (UsbVendors.TryGetValue(vendorId, out var uv2))
                return uv2;
        }
        return NormalizeVendorName(fallbackManufacturer);
    }

    public static string LookupSubsystemOem(string subsysVendorId)
    {
        if (string.IsNullOrWhiteSpace(subsysVendorId)) return "";
        return OemSubsystemVendors.TryGetValue(subsysVendorId, out var name) ? name : "";
    }

    public static string NormalizeVendorName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
        var s = raw.Trim();
        if (s.Contains("Acer", StringComparison.OrdinalIgnoreCase)) return "Acer";
        if (s.Contains("Dell", StringComparison.OrdinalIgnoreCase) || s.Contains("Alienware", StringComparison.OrdinalIgnoreCase)) return "Dell";
        if (s.Contains("Lenovo", StringComparison.OrdinalIgnoreCase)) return "Lenovo";
        if (s.Contains("Hewlett", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(s, @"\bHP\b", RegexOptions.IgnoreCase)) return "HP";
        if (s.Contains("ASUSTeK", StringComparison.OrdinalIgnoreCase) || s.Contains("ASUS", StringComparison.OrdinalIgnoreCase)) return "ASUS";
        if (s.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(s, @"\bMSI\b", RegexOptions.IgnoreCase)) return "MSI";
        if (s.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase)) return "Gigabyte";
        if (s.Contains("ASRock", StringComparison.OrdinalIgnoreCase)) return "ASRock";
        if (s.Contains("Razer", StringComparison.OrdinalIgnoreCase)) return "Razer";
        if (s.Contains("Framework", StringComparison.OrdinalIgnoreCase)) return "Framework";
        if (s.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return "Microsoft";
        if (s.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        if (s.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(s, @"\bAMD\b", RegexOptions.IgnoreCase)) return "AMD";
        if (s.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "Intel";
        if (s.Contains("Realtek", StringComparison.OrdinalIgnoreCase)) return "Realtek";
        if (s.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase)) return "Qualcomm";
        if (s.Contains("MediaTek", StringComparison.OrdinalIgnoreCase)) return "MediaTek";
        if (s.Contains("Broadcom", StringComparison.OrdinalIgnoreCase)) return "Broadcom";
        if (s.Contains("Focusrite", StringComparison.OrdinalIgnoreCase)) return "Focusrite";
        if (s.Contains("Samsung", StringComparison.OrdinalIgnoreCase)) return "Samsung";
        if (s.Contains("Logitech", StringComparison.OrdinalIgnoreCase)) return "Logitech";
        if (s.Contains("Corsair", StringComparison.OrdinalIgnoreCase)) return "Corsair";
        return s;
    }

    public static string GetOemSupportUrl(string oem, string model)
    {
        var q = Uri.EscapeDataString(string.IsNullOrWhiteSpace(model) ? "" : model.Trim());
        return oem.ToLowerInvariant() switch
        {
            "acer" => $"https://www.acer.com/us-en/support/drivers-and-manuals?search={q}",
            "dell" => $"https://www.dell.com/support/home/en-us/products?app=drivers&search={q}",
            "lenovo" => $"https://pcsupport.lenovo.com/us/en/search?query={q}",
            "hp" => $"https://support.hp.com/us-en/drivers/results?q={q}",
            "asus" => $"https://www.asus.com/support/download-center/",
            "msi" => $"https://www.msi.com/support/download/search?q={q}",
            "gigabyte" => $"https://www.gigabyte.com/Support/Consumer/Download",
            "asrock" => $"https://www.asrock.com/support/index.asp",
            "razer" => $"https://mysupport.razer.com/app/answers/list/kw/{q}",
            "framework" => $"https://knowledgebase.frame.work/en_us/bios-and-drivers-downloads-rJ3PaCexh",
            "microsoft" => $"https://support.microsoft.com/en-us/surface/download-drivers-and-firmware-for-surface-09bb2e09-2a4b-cb69-0951-078a7739e120",
            "samsung" => $"https://www.samsung.com/us/support/downloads/",
            _ => string.IsNullOrWhiteSpace(model)
                ? "https://www.catalog.update.microsoft.com/"
                : $"https://www.google.com/search?q={Uri.EscapeDataString($"{oem} {model} drivers download")}"
        };
    }

    public static string GetDirectManufacturerUrl(string siliconVendor, string category, string deviceName, bool isLaptop)
    {
        var v = siliconVendor.ToLowerInvariant();
        if (v.Contains("nvidia"))
            return "https://www.nvidia.com/en-us/drivers/";
        if (v.Contains("amd"))
            return "https://www.amd.com/en/support/download/drivers.html";
        if (v.Contains("intel"))
        {
            if (category == "Display")
                return "https://www.intel.com/content/www/us/en/download/785597/intel-arc-iris-xe-graphics-windows.html";
            if (category is "Network" or "Bluetooth")
                return "https://www.intel.com/content/www/us/en/download-center/home.html";
            return "https://www.intel.com/content/www/us/en/support/detect.html";
        }
        if (v.Contains("realtek"))
            return "https://www.realtek.com/Download/Index?cate_id=193";
        if (v.Contains("focusrite"))
            return "https://downloads.focusrite.com/focusrite";
        if (v.Contains("qualcomm"))
            return "https://www.catalog.update.microsoft.com/Search.aspx?q=Qualcomm";
        if (v.Contains("mediatek"))
            return "https://www.catalog.update.microsoft.com/Search.aspx?q=MediaTek";
        if (v.Contains("samsung"))
            return "https://semiconductor.samsung.com/consumer-storage/support/tools/";
        if (v.Contains("logitech"))
            return "https://support.logi.com/hc/en-us/articles/360024361233";
        if (v.Contains("corsair"))
            return "https://www.corsair.com/us/en/s/downloads";
        if (v.Contains("razer"))
            return "https://www.razer.com/synapse-4";
        if (v.Contains("broadcom"))
            return "https://www.broadcom.com/support/download-search";

        var q = Uri.EscapeDataString($"{siliconVendor} {deviceName} windows driver{(isLaptop ? " laptop" : "")}");
        return $"https://www.catalog.update.microsoft.com/Search.aspx?q={q}";
    }

    public static string GetMicrosoftCatalogUrl(DriverDevice device)
    {
        // Best query for MS Update Catalog is the primary Hardware ID (VEN&DEV&SUBSYS or VEN&DEV)
        var bestHwid = device.HardwareIds.FirstOrDefault(h =>
            h.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) ||
            h.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
            h.StartsWith("HDAUDIO\\", StringComparison.OrdinalIgnoreCase))
            ?? device.HardwareId;

        if (!string.IsNullOrWhiteSpace(bestHwid))
        {
            // Strip &REV_xx for broader exact catalog hits if present
            var cleaned = Regex.Replace(bestHwid, @"&REV_[0-9A-Fa-f]+", "", RegexOptions.IgnoreCase);
            return $"https://www.catalog.update.microsoft.com/Search.aspx?q={Uri.EscapeDataString(cleaned)}";
        }

        return $"https://www.catalog.update.microsoft.com/Search.aspx?q={Uri.EscapeDataString(device.DisplayName)}";
    }
}
