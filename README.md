# xDriverUpdater

**Smart Hardware-Aware Windows Driver Manager & Dual-Source Updater — Zero Background Bloat.**

Built to feel like a native Microsoft / `xPrograms` flagship utility, **xDriverUpdater** inspects your entire Windows hardware topology, understands the difference between **Desktop PCs** and **OEM-Customized Laptops** (e.g., an Acer laptop whose Intel/NVIDIA GPU carries PCI Subsystem Vendor ID `1025`), and gives you full control over how drivers are sourced, verified, backed up, and installed.

## Key Capabilities

- **Dual-Mode Update Engine**:
  1. **Windows Update (Device Manager Channel)**: Queries, downloads, and installs WHQL-signed drivers via the exact same `Microsoft.Update.Session` (WUA COM) engine that Windows Device Manager uses, plus direct Hardware-ID links to the Microsoft Update Catalog.
  2. **Direct Manufacturer & Live Vendor Feeds**: Live NVIDIA official DCH WHQL lookup (Desktop vs Notebook aware, automatically converting Windows `32.0.15.xxxx` versions to NVIDIA `5xx.xx` versions) plus direct vendor/OEM portals for AMD, Intel, Realtek, Focusrite, Qualcomm, MediaTek, Samsung, and all major laptop/board OEMs.
- **Smart Laptop OEM Guard**:
  - Reads `Win32_SystemEnclosure`, `Win32_ComputerSystem`, `Win32_Battery`, and **PCI Subsystem Vendor IDs (`SUBSYS_xxxxVVVV`)**.
  - Detects when a laptop GPU, audio, or wireless chip is OEM-customized (e.g. Acer `1025`, Dell `1028`, Lenovo `17AA`, HP `103C`, ASUS `1043`, MSI `1462`, Razer `1A58`).
  - Advises OEM / Windows Update DCH drivers first on laptops to preserve MUX/hybrid graphics, backlight, hotkey, and thermal tuning — while still letting you override to Direct Manufacturer anytime.
- **Extensible Pluggable Repositories**:
  - Add unlimited **JSON Repository URLs** (GitHub Raw, Cloudflare R2, self-hosted `xServer`, or local `.json` files) OR point directly to a **local/network folder** full of `.inf` drivers (xDriverUpdater auto-indexes Hardware IDs!).
  - Supports wildcard Hardware ID matching (`PCI\VEN_8086&DEV_*&SUBSYS_*1025*`), OEM filtering, Form Factor filtering (`laptop` | `desktop` | `any`), optional `SHA-256` verification, and automated `.zip` / `.cab` / `.inf` / `.exe` / `.msi` installation.
  - Export a ready-to-edit sample `manifest.json` right from the Repositories tab.
- **Enterprise-Grade Safety & Driver Store Manager**:
  - Automatic **Windows System Restore Point** creation before installs.
  - One-click **Full Third-Party Driver Backup** (`pnputil /export-driver *`) and automatic single-INF backup before replacing a driver.
  - **Driver Store Browser** (`pnputil /enum-drivers`) flagging `ACTIVE`, `CACHED`, and `STALE` (superseded) OEM packages so you can safely prune DriverStore bloat.
  - **Local Package Installer** for any `.inf`, `.cab`, `.zip`, or driver folder.
- **CLI Mode**:
  ```powershell
  xDriverUpdater.exe --audit          # Hardware, OEM/Laptop detection & smart audit
  xDriverUpdater.exe --scan           # Scan Windows Update + Direct + Repositories
  xDriverUpdater.exe --backup [dir]   # Export all third-party drivers via pnputil
  xDriverUpdater.exe --list-repos     # List configured repositories
  ```

## Build & Package

```powershell
.\.build.ps1                   # Build Release & deploy to %LOCALAPPDATA%\xPrograms\xDriverUpdater
.\.build.ps1 -Clean -Run       # Clean rebuild, deploy, and launch GUI
.\.build-Installer.ps1         # Build standalone Inno Setup installer (xDriverUpdater-Setup.exe)
```
