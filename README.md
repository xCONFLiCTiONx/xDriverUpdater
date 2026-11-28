# xSystemForge

**All-in-One Windows Driver, Telemetry, Debloat, Privacy & Smart Power Plan Suite — Zero Background Bloat.**

Built to feel like a native Windows utility, **xSystemForge** unifies driver management, telemetry hardening & Appx debloating, live outbound connection monitoring, and hardware-aware Windows power plan generation into a single dark-mode desktop application.

## Key Capabilities

### 1. Driver Manager & Dual-Source Updater
- **Dual-Mode Update Engine**:
  - **Windows Update (Device Manager Channel)**: Queries, downloads, and installs WHQL-signed drivers via `Microsoft.Update.Session` (WUA COM) plus direct Microsoft Update Catalog matching.
  - **Direct Manufacturer & Live Vendor Feeds**: Live NVIDIA official DCH WHQL lookup (Desktop vs Notebook aware) plus direct vendor/OEM portals for AMD, Intel, Realtek, Focusrite, Qualcomm, MediaTek, Samsung, and major laptop/board OEMs.
- **Smart Laptop OEM Guard**: Reads `Win32_SystemEnclosure`, `Win32_ComputerSystem`, `Win32_Battery`, and PCI Subsystem Vendor IDs (`SUBSYS_xxxxVVVV`) to protect OEM-customized laptop GPUs, audio, and wireless chips.
- **Extensible Pluggable Repositories**: Supports HTTPS JSON manifests, Dell DriverPackCatalog XML, and local driver folders auto-indexed by Hardware ID.
- **Driver Store & Safety**: System Restore Point creation, full third-party driver backup (`pnputil /export-driver *`), portable offline pack export, stale Driver Store package cleanup, and local `.inf`/`.cab`/`.zip` installation.

### 2. Telemetry, Debloat & Live Outbound Monitor (Merged from xTelemetry)
- **Telemetry & Debloat Control**: Audits and toggles Windows & vendor telemetry services, scheduled tasks, registry policies, startup items, and Appx bloatware one-by-one or in safe batches with full restore support.
- **Live Outbound Monitor**: Real-time outbound TCP connection table with reverse DNS resolution, suspected telemetry flagging, one-click Windows Firewall outbound blocking, and process termination.

### 3. Hardware-Aware Power Plan Generator & Enforcer (Merged from xPowerPlan)
- **Hardware & Sleep State Audit**: Inspects CPU (AMD Ryzen / CPPC2 aware), GPU & BIOS-disabled iGPU status, NVMe/SATA/USB storage bus topology, S3/S0/Hibernate/Hypervisor sleep capabilities, and audits the active Windows power plan.
- **Hardware-Tuned Presets & Full Tuning**: `Smart Dev & AI`, `Max Performance`, `Cool & Quiet`, and `Custom` presets with granular control over Display/Sleep/xTaskForge keep-awake policies, CPU Boost Mode, EPP, Core Parking, PCIe ASPM, SATA AHCI, NVMe Idle/Latency, and USB 3 power management.
- **Live Diff, Read-Back Verification & Plan Manager**: Compares current active settings against target values, enforces via native `powercfg` with `/qh` read-back verification (zero background daemon), and lets you switch, export `.pow` backups, or delete unused Windows power plans.

### 4. Context Menus, Startup, Installed Apps & Win+X Manager (Merged from xContextGuard)
- **Explorer Context Menus**: Inspect, edit, enable/disable, or delete static shell verbs and COM shell extension handlers across File, Folder, Folder Background, Drive, and Desktop Background menus.
- **Startup Manager**: Manage HKCU/HKLM `Run`/`RunOnce` (64-bit & 32-bit) and Current User/All Users Startup Folder items with Windows `StartupApproved` support.
- **Installed Applications & Orphan Detection**: Lists installed applications from HKLM/HKCU Uninstall keys, flags orphaned entries with missing files, and supports editing, hiding, or deleting entries.
- **Win+X Menu & JSON Backups**: Rename, move between groups, or toggle Windows 11 Win+X shortcuts with one-click Explorer restart and automatic JSON backup/restore before every change.

## CLI Mode

```powershell
xSystemForge.exe --audit                          # Hardware, OEM/Laptop detection & driver audit
xSystemForge.exe --scan                           # Scan Windows Update + Direct + Repositories
xSystemForge.exe --backup [dir]                   # Export all third-party drivers via pnputil
xSystemForge.exe --list-repos                     # List configured repositories
xSystemForge.exe --power-audit                    # Inspect hardware and audit active power plan
xSystemForge.exe --power-list                     # List all Windows power plans
xSystemForge.exe --power-apply <dev|perf|quiet>   # Generate, apply & verify power preset (alias: --apply)
```

## Build & Package

```powershell
.\.build.ps1                   # Build Release & deploy to %LOCALAPPDATA%\xPrograms\xSystemForge
.\.build.ps1 -Clean -Run       # Clean rebuild, deploy, and launch GUI
.\.build-Installer.ps1         # Build standalone Inno Setup installer (xSystemForge-Setup.exe)
```
