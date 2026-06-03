# Asus Fan & CPU Controller Pro — Ultimate AI Handover & Knowledge Base

This document serves as the absolute guide for any future AI assistant, developer, or system administrator who takes over the maintenance, deployment, or porting of the **Asus Fan & CPU Controller Pro** project. 

If you are shifting to a new laptop, diagnosing a frequency cap, or modifying the fan curves, **read this file first**. It documents every architectural decision, key system integration, driver mechanics, and resolved bugs.

---

## 📖 Project Overview & Architecture

**Asus Fan & CPU Controller Pro** is a native C# WinForms application designed for Asus ROG/TUF gaming laptops (and other compatible Asus systems). It replaces heavy, bloated OEM software (like Armoury Crate / G-Helper) and utility software (like ThrottleStop / SpeedFan) by providing:
1. **Dynamic Fan Speed Control**: Maps customized Temperature-to-RPM curves and writes speed values directly to the Asus embedded controller via a low-level kernel driver.
2. **Dynamic CPU Frequency Sync**: Automatically limits or unlocks the CPU's target clock speed, Intel HWP energy/performance behaviors, and core idle states based on real-time CPU loads and custom-drawn Frequency Curves.
3. **Optimized Multi-Threaded I/O**: Keeps GUI rendering fluid (60+ FPS) while handling slow hardware driver queries.
4. **Silent UAC-Bypassing Startup**: Launches automatically at Windows logon and runs with full Administrator privileges without ever displaying UAC prompt dialogs.

```mermaid
graph TD
    UIThread[UI Render Thread: 60+ FPS] <-->|Thread-Safe StatsLock| StatsCache[(Shared Cached Stats)]
    UIThread -->|TargetLock| TargetState[(Shared Target State)]
    
    subgraph BackgroundWorker [Background Hardware Worker Thread]
        Loop[High-Priority Loop: 100ms cycle]
        AsusDriver[Asus WinIO64 Driver]
        PowerCfg[Windows Powercfg API]
    end
    
    TargetState -->|Read Targets| Loop
    Loop -->|Query Temp & RPM every 1s| AsusDriver
    Loop -->|Query CPU Load every 100ms| StatsCache
    Loop -->|Write Fan Speed on change| AsusDriver
    Loop -->|Apply 6 Power settings every 5s or on change| PowerCfg
    AsusDriver -->|Update Cache| StatsCache
```

---

## ⚡ CPU Frequency & Power Throttling: The Complete Solution

Historically, gaming laptops suffer from aggressive thermal throttling, stuck clocks (e.g., locked at 1.0 GHz), or mismatched effective clock speeds. We implemented a robust, fully automated solution in C# that mimics ThrottleStop's core behaviors directly in our app.

### Why standard Win32 Power APIs Failed
Initially, we implemented Win32 API calls (`PowerWriteACValueIndex` and `PowerSetActiveScheme` in `powrprof.dll`) to avoid process-spawning overhead. However, we discovered that **the OS was ignoring direct memory-based writes for several critical settings**, resulting in the CPU remaining throttled or stuck at 1.0–1.3 GHz under load. 

**The fix:** We transitioned back to executing `powercfg` commands sequentially inside a single `cmd.exe /c` shell process chain. To prevent system micro-stutters, we capped these calls to run **only upon limit changes** OR **unconditionally once every 5 seconds** (via a loop counter threshold).

### The Six (6) Power Settings Managed by the App
When a target frequency is active (or set to "Max"), the application instantly applies **six distinct Windows Power Scheme settings** across AC (Plugged-In) and DC (Battery) profiles:

| Power Setting | Registry / Power GUID | Max Performance Mode | Hard Cap Mode (e.g. 2.8 GHz) | Description |
|---|---|---|---|---|
| **PROCFREQMAX** | `75b0ae3f-bce0-45a7-8c89-c9611c25e100` | `0` (Unlimited) | `2800` (e.g. 2800 MHz) | Hard ceiling for maximum processor core frequency. |
| **PROCTHROTTLEMAX** | `bc5038f7-23e0-4960-96da-33abaf5935ec` | `100` (%) | `100` (%) | Shadow limit. Must *always* be 100% to avoid OS throttling overrides. |
| **PROCTHROTTLEMIN** | `893dee8e-2bef-41e0-89c6-b55d0929964c` | `100` (%) | `5` (%) | Forces CPU cores to stay active under Max mode, allowing idle down-clocks under limited modes. |
| **PERFBOOSTMODE** | `be337238-0d82-4146-a960-4f3749d470c7` | `2` (Aggressive) | `0` (Disabled) | Controls Intel Turbo Boost behavior. Off in cap mode lowers temps dramatically. |
| **PERFEPP** | `36687f9e-e3a5-4dbf-b1dc-15eb381c6863` | `0` (Max Performance) | `100` (Max Power Saving) | Energy Performance Preference (EPP) for Intel Speed Shift / HWP. |
| **IDLEDISABLE** | `5d76a2ca-e8c0-402f-a133-2158492d58ad` | `1` (C-states Disabled) | `1` (C-states Disabled) | Disables processor idle C-states (similar to ThrottleStop's "Disable Idle" / "C1E"). |

### 🎯 The "Effective Clock" Core Temp Bug & Solution
A major issue arose where Task Manager reported `4.07 GHz` but Core Temp showed **Effective Clock: 2270 MHz** (a ~45% performance drop under 60% load). 

* **Cause**: When idle states (C-states) are active, Windows core-parks or sleeps CPU execution pipelines during idle cycles. Core Temp calculates the "Effective Clock" based on actual executed cycles divided by wall time, reflecting the sleep periods, while Task Manager reports the target hardware multiplier.
* **Solution**: By setting **IDLEDISABLE = 1** (C-states disabled) across both Max and Hard Cap modes, the CPU pipelines never enter sleep states. The **Effective Clock immediately locked 1:1 to the actual target speed**, providing rock-solid, latency-free gaming frame rates and high computation speeds.

### 🛡️ Startup Safety & Throttling Prevention (Added June 2026)

When the application is registered to run on Windows Startup (`schtasks`), dynamic frequency controls could immediately lock the CPU to its lowest configured frequencies. On certain modern Intel architectures, setting `PERFEPP` to `100` (Max Power Saving) combined with `PROCTHROTTLEMIN` at `5%` caused the processor to aggressively lock down to its absolute hardware floor of **0.4 GHz (400 MHz)**. This makes Windows boot cycles painfully slow and crashes the user shell (`explorer.exe`).

We implemented two primary architectural guards:
1. **60-Second Startup Grace Period**: Inside [Form1.cs](file:///C:/Users/saksh/OneDrive/Documents/AsusFanControl-master/AsusFanControlGUI/Form1.cs), we track `appStartupTime`. For the first 60 seconds of the program's lifecycle, the background thread overrides all frequency limit targets to `0` (Max performance / Unlimited) and keeps C-states active. The UI reflects this with `Limit: Startup Grace (Xs)`. This allows Windows to fully boot, initialize core services, and launch the user desktop at full speed.
2. **Safe Throttling Floors**: In limited modes (when `mhz > 0`), we raised `minState` from `5` to `35` and set `epp` to `50` (Balanced/Dynamic) instead of `100`. This ensures that even when dynamic sync downclocks the CPU under low load, it stays at a safe, responsive clock speed (typically >= 1.0 GHz to 1.4 GHz depending on nominal base clock) and responds dynamically to demand up to our set limit.

---

## ⚙️ ThrottleStop Wrapper Integration

To solve aggressive hardware-level performance caps (like Intel Tiger Lake PL1 limits) that cannot be controlled via OS power GUIDs or `powercfg` alone, we integrated **ThrottleStop** directly into the application:

1. **How it works**:
   - On startup, the C# GUI searches for `ThrottleStop.exe` inside its own base directory, a `ThrottleStop` subdirectory, and their parent directories.
   - If found, it cleanly kills any existing orphan instances of `ThrottleStop` and launches a fresh instance programmatically.
   - To keep things clean, it launches ThrottleStop **minimized** (`ProcessWindowStyle.Minimized`) so it immediately resides in the system tray.
   - When the Asus Fan Control application is closed, it intercepts the exit event and cleanly terminates the `ThrottleStop` process to prevent background leaks.
2. **Elevated UAC-Bypass Synergy**:
   - Writing to CPU Model Specific Registers (MSRs) requires Administrator rights, meaning running ThrottleStop manually triggers a UAC prompt.
   - By running our `AsusFanControlGUI` through the elevated **Scheduled Task**, our application is already elevated.
   - When our C# application starts `ThrottleStop.exe`, ThrottleStop **automatically inherits our elevated security token**.
   - This provides **seamless, zero-UAC, completely silent hardware performance tuning** on startup!
3. **Paths Configured**:
   - `C:\Users\saksh\OneDrive\Documents\AsusFanControl-master\ThrottleStop\ThrottleStop.exe` (Project root directory)
   - `C:\Users\saksh\OneDrive\Documents\AsusFanControl-master\AsusFanControlGUI\bin\Release\net7.0-windows\win-x64\publish\ThrottleStop\ThrottleStop.exe` (Build publish directory)

---

## 🚀 Resolving the Mouse-Dragging Lag Bug

* **The Problem**: Clicking and dragging points on the Fan or Frequency curve felt extremely choppy, causing the cursor to lag, freeze, or jump by 1–2 seconds.
* **The Root Cause**: Reading hardware sensors (temperatures, fan RPMs, CPU load metrics) requires querying the low-level motherboard embedded controller via the WinIO driver. These BIOS/driver reads are synchronous and block execution for 100–300ms. Running them on the main GUI UI thread starved the application of mouse movement windows event messages, resulting in extreme lag.
* **The Architecture Fix**: We separated concerns completely:
  - We launched a dedicated thread `bgThread` marked with high priority (`AboveNormal`).
  - The worker loop runs continuously every 100ms. It reads temperatures/RPMs every 1s, and applies power limits only on changes or every 5s.
  - Sensor results are written to shared variables wrapped in a thread-safe `statsLock`.
  - The WinForms UI thread reads from the cache instantly on its timer tick, maintaining smooth **60+ FPS UI rendering** even when low-level driver calls take hundreds of milliseconds in the background!

---

## 🔒 Silent UAC-Bypass Startup & Desktop Shortcut

Because writing directly to Asus hardware ports and altering Windows Power Schemes requires Kernel-level access, the application **must run with full Administrator privileges**. 

To bypass the annoying Windows User Account Control (UAC) prompt at boot or when double-clicking the desktop icon, we developed a native bypass workflow using Task Scheduler.

### Script Files Available in Root:
1. **[register_native_task.ps1](file:///C:/Users/saksh/OneDrive/Documents/AsusFanControl-master/register_native_task.ps1)**:
   - Configures a Scheduled Task named `AsusFanControlProStartup`.
   - Action targets the published single-file EXE.
   - Sets the working directory to the publish folder (critical for the `AsusWinIO64.dll` driver dependency).
   - Configures it to run `AtLogOn` with `Highest` privileges.
   - Recreates the Desktop Shortcut to launch: `schtasks.exe /run /tn "AsusFanControlProStartup"`.
   - Bypasses UAC entirely because Task Scheduler launches the process under an already approved elevated security context!
2. **[restore_normal_launch.ps1](file:///C:/Users/saksh/OneDrive/Documents/AsusFanControl-master/restore_normal_launch.ps1)**:
   - Deletes the Scheduled Task.
   - Reverts the Desktop Shortcut back to running the executable file directly.

---

## 🛠️ Build, Compile & Deploy Guide

When migrating to a new machine or compiling code edits:

### Pre-requisites
- **SDK**: Download and install [.NET 7.0 SDK](https://dotnet.microsoft.com/download) (or newer).
- **Target Platform**: Must target Windows x64 (due to the C++ pre-compiled native driver `AsusWinIO64.dll`).

### Build Command (Single-File Release)
Run this command from your terminal in the directory containing `AsusFanControlGUI.csproj`:

```powershell
Stop-Process -Name AsusFanControlGUI -Force -ErrorAction SilentlyContinue
dotnet publish "AsusFanControlGUI.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -o "bin\Release\net7.0-windows\win-x64\publish"
```

### Critical Files in Publish Folder
Ensure the following files remain side-by-side in the output publish folder (`bin\Release\net7.0-windows\win-x64\publish\`):
- `AsusFanControlGUI.exe` — The compiled single-file C# application.
- `AsusWinIO64.dll` — The Asus Kernel driver wrapper. **If this file is missing, the application will crash at startup when attempting to bind to the kernel ports.**
- `settings.txt` — Stores user configurations (fan curves, temperature safety thresholds).

---

## 🛡️ Autonomous Self-Healing & Portability Mechanics

To make the application completely portable across new laptops and immune to accidental file deletions or directory shifts, we implemented an autonomous **self-healing startup sequence** directly in `Program.cs`:

1. **Strict Dependency Verification**:
   - Before executing any application logic, `Program.Main()` verifies the physical presence of `AsusWinIO64.dll` in the application directory.
   - If missing, it halts execution and launches a professional MessageBox advising the user to restore the driver file side-by-side, preventing unhandled runtime exceptions or silent failures.
2. **Dynamic Task Scheduler Validation**:
   - When run with Administrator privileges (but not yet as `SYSTEM`), `Program.HealScheduledTaskAndShortcut()` performs a background query: `schtasks.exe /query /xml /tn "AsusFanControlProStartup"`.
   - It reads the XML output and parses the program path.
   - If the task **does not exist** or the **command path has changed** (e.g., the folder was renamed, moved to a different drive, or you are running on a brand-new computer):
     - The C# program dynamically generates a pristine XML representation for Task Scheduler.
     - It writes it to a temporary file and imports it using `schtasks.exe /create /tn "AsusFanControlProStartup" /xml "temp_xml_path" /f`.
     - This automatically configures logon-triggers, highest-privilege elevation, disables battery execution blocks, and matches the correct executable directory.
3. **Dynamic Desktop Shortcut Reconstruction**:
   - It reconstructs the Desktop shortcut (`Asus Fan & CPU Controller Pro.lnk`) silently via PowerShell to target `schtasks.exe /run /tn "AsusFanControlProStartup"`, embedding the native executable's propeller icon location.
4. **Porting to a New PC Checklist**:
   - Simply copy the publish directory (`win-x64\publish`) containing all dependencies (`AsusFanControlGUI.exe`, `AsusWinIO64.dll`, `PsExec.exe`, `ThrottleStop`) to any folder on the new PC.
   - **Right-click `AsusFanControlGUI.exe` and select "Run as Administrator" ONCE**.
   - The application will **fully configure the Windows Task Scheduler, create the Desktop shortcut, and self-heal itself completely in the background**. No manual scripts or AI instructions are required!

---

## 🧠 Diagnostic Checklist for Future AIs

If the user reports that frequency control, speed limits, or fan curves are not applying:

1. **Verify Administrator Elevation**:
   - Check if the application title bar shows `(Admin)`. If not, the `powercfg` commands will fail.
   - Run `register_native_task.ps1` via an elevated PowerShell prompt to ensure Task Scheduler permissions are active.
2. **Check for Running Instances**:
   - Multiple background instances can lock the Asus WinIO interface. Kill all instances:
     `Stop-Process -Name AsusFanControlGUI -Force -ErrorAction SilentlyContinue`
3. **Verify Power Guid Support**:
   - Run `powercfg /query` in command prompt to ensure the active power scheme exists and supports processor performance settings.
   - If the power schemes get corrupted, reset the CPU power scheme values back to defaults in Windows:
     `powercfg -restoredefaultschemes`
4. **Driver Signature Issues**:
   - If shifting to a new laptop and the app crashes immediately upon reading temperature, secure boot or device guard policy may be blocking the `AsusWinIO64.dll` driver. Check the Windows Event Viewer under System logs.

---

## 🧬 AMD Ryzen Compatibility & Standalone Portability (New!)

To make the application completely portable, safe, and compatible with both high-performance Intel systems and AMD Ryzen laptop architectures (such as the Ryzen 3 7320U), we implemented a dual-mode engine:

1. **Automatic CPU Architecture Detection**:
   - At startup, the program queries environment variables (`PROCESSOR_IDENTIFIER` and `PROCESSOR_ARCHITECTURE`) via `IsAmdProcessor()`.
   - If an AMD Ryzen CPU is detected (`AuthenticAMD`, `AMD`), it sets the class-level state flag `isAmdProcessor = true`.
2. **Dynamic ThrottleStop & Intel Feature Bypass**:
   - Since ThrottleStop is strictly Intel-only, trying to launch it on AMD Ryzen will trigger physical hardware unsupported failures. The program automatically skips searching for or launching ThrottleStop on AMD hardware.
   - Dynamic power configurations (like Intel HWP Speed Shift and dynamic C-states) are safely encapsulated in exception boundaries, ensuring AMD models do not trigger OS power scheme errors.
3. **Asus EC Driver Portability (VivoBook Verification)**:
   - Asus AMD Ryzen Vivobooks utilize the identical Embedded Controller (EC) cooling loop and health table registers as Intel Asus Vivobooks. 
   - Consequently, the direct hardware queries (`AsusWinIO64.dll`'s `GetFanSpeeds()`, `SetFanSpeeds()`, and `Thermal_Read_Cpu_Temperature()`) are fully operational and verified to work natively on both platforms!
4. **Resilient High-Precision Temperature Fallback**:
   - In case a specific Asus motherboard BIOS locks down direct driver temperature reads, we integrated a **three-stage temperature sensor fallback** in `GetCpuTemperature()`:
     1. Primary: Direct physical read via `AsusWinIO64.dll`.
     2. Secondary: WMI query `MSAcpi_ThermalZoneTemperature` under the `root\WMI` namespace.
     3. Tertiary: WMI query `Win32_PerfFormattedData_Counters_ThermalZoneInformation` under `root\CIMV2`.
     4. Emergency Load-Based Estimation: If all BIOS queries fail, it dynamically estimates a temperature based on active CPU load: `estimatedTemp = 40 + (int)(load * 0.4)`. This guarantees the cooling fans will always physically speed up and cool the laptop under heavy compute cycles!
5. **No-PsExec Standalone Fallback**:
   - If `PsExec.exe` is absent from the publish directory, the application gracefully skips the high-privilege SYSTEM scheduled task startup sequence.
   - Instead, it falls back to standard Windows **UAC Elevation (`runas` verb)**. It will display a standard UAC prompt at startup and run directly as an Administrator. This makes it perfect for sharing with non-technical users who want a clean standalone program without configuring task schedulers!

---

## 🧵 Low-Level Driver Thread Affinity Rules (Critical!)

During optimization, we encountered a crucial system driver bug: the low-level physical port/memory mapper `AsusWinIO64.dll` is **extremely sensitive to Thread Affinity**.

* **The Problem**: Drivers that map physical hardware memory space using `MapPhysToLinear` bind their operational handles and mapping contexts strictly to the thread that initialized it. 
* **The Symptom**: If `AsusControl` is initialized on the **Main UI Thread**, and a separate **Background Thread** tries to execute reads or writes (like `GetFanSpeeds()` or `SetFanSpeeds()`), the operations will fail silently at the kernel level.
* **The Rule**: Any future AI or developer making changes to low-level physical registers **MUST** ensure that all driver initialization, read queries, and write operations are executed on the same thread context. If a background worker thread is used for hardware I/O, both the `new AsusControl()` initialization and all operational loops must be placed inside that same background thread sequence!
