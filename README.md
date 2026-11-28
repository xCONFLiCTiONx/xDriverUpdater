# xSecurityCenter

**All-in-One Always-On Windows Security, Privacy, Network, Drive Health, Driver, Power Plan & System Suite.**

Styled with the **Antigravity Dark Gray + Cloudflare Orange** navigation-drawer design system (matching `xBackup` & `xPrivacyGuard` for Windows), **xSecurityCenter** stays running quietly in the system tray as your unified Windows security hub.

## Key Capabilities

1. **Always-On Background Security Center & `xPrivacyGuard` Watchdog**
   - Minimizes to the system tray on close, supports elevated logon startup (`--silent`), and enforces single-instance activation.
   - Continuously monitors `xPrivacyGuard` process & MASQUE tunnel status, with one-click launch and tray alerts if it stops.
2. **Offline Network Diagnostics & Emergency Dual-Adapter Static IP Troubleshooter**
   - Runs a 6-step local diagnostic suite with zero internet or AI needed (physical NIC link & APIPA check, `xPrivacyGuard` tunnel/kill-switch check, gateway ping, DNS reachability, public IP routing, and DNS resolution) with plain-English root cause & recovery steps.
   - One-click **Apply Static IP to Both Adapters** (Ethernet & Wi-Fi) with automatic pre-change snapshot and one-click **Revert Both Adapters (Restore Previous / DHCP)**, plus Flush DNS, Renew DHCP, Restart Adapters, and Reset Winsock/TCP-IP.
3. **Drive Sentinel (NVMe & SATA SMART Failure Prediction)**
   - Direct IOCTL NVMe health log & SAT-12/SAT-16/ATA SMART telemetry, 30-day failure prediction, temperature monitoring, wear-rate history, and correlated Windows storage event logs with background tray alerts.
4. **Driver Manager, Telemetry & Debloat, Live Outbound Monitor, Smart Power Plan & Context/Startup Manager**
   - Dual-source driver updater (Windows Update WHQL, MS Catalog, NVIDIA Live API, custom repos), Driver Store cleanup, Telemetry & Appx debloat, real-time outbound TCP firewall monitor, hardware-aware Power Plan enforcer (`powercfg`), and Explorer Context Menu / Startup / Installed Apps / Win+X manager.

## Build & Package

```powershell
.\.build.ps1                   # Build Release & deploy to %LOCALAPPDATA%\xPrograms\xSecurityCenter
.\.build.ps1 -Clean -Run       # Clean rebuild, deploy, and launch GUI
.\.build-Installer.ps1         # Build standalone signed installer (xSecurityCenter-Setup.exe)
```
