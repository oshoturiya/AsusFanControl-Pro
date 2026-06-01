# AsusFanControlGUI Dragging Smoothness & Latency Fix Plan

The goal of this plan is to eliminate the 1-to-2 second delay/lag when dragging control points (dots) back and forth on the Fan and Frequency charts, achieving a buttery-smooth, ultra-responsive 60+ FPS dragging experience.

## User Review Required

> [!IMPORTANT]
> To achieve absolute visual fluidity and eliminate dragging stutters:
> 1. We will **move all blocking hardware I/O** (driver temperature reads, fan speed queries, and fan speed writes) off the main Windows Forms UI thread and onto a dedicated high-priority **Background Hardware Worker Thread**.
> 2. We will **eliminate external `powercfg.exe` process spawns** entirely. Running `powercfg.exe` in the background still spawns multiple processes per second, which causes massive system-wide process scheduling queues and delay. Instead, we will declare and use native Win32 **Power Profile P/Invoke APIs** (`powrprof.dll`), which complete in **less than 1 millisecond** (compared to 500ms+ for multiple `powercfg` processes).
> 3. The UI thread will continuously read the latest hardware stats from lightweight, thread-safe, lock-free cached memory variables, allowing the chart rendering to run at the display's maximum refresh rate.

## Open Questions

None. The hardware bottlenecks are clear and the optimization strategy is mathematically sound.

## Proposed Changes

---

### [AsusFanControlGUI]

We will modify the core GUI form to integrate a background worker thread and implement the Win32 P/Invoke declarations.

#### [MODIFY] [Form1.cs](file:///C:/Users/saksh/OneDrive/Documents/AsusFanControl-master/AsusFanControlGUI/Form1.cs)
- **Win32 Power Profile P/Invoke Declarations**: Import `PowerWriteACValueIndex`, `PowerWriteDCValueIndex`, `PowerSetActiveScheme`, `PowerGetActiveScheme`, and `LocalFree` from `powrprof.dll` and `kernel32.dll`.
- **Background Hardware Worker Thread**: Add a dedicated background thread that loops every 250ms:
  - Fetches the CPU Load using the performance counter.
  - Every 1 second (4 ticks), queries the CPU Temperature and Fan RPMs from the `AsusControl` driver (preventing excessive driver query overhead).
  - Checks if a new Fan Speed or CPU Frequency needs to be applied, and writes them to the hardware/OS.
- **Thread-Safe Shared States**: Keep simple thread-safe shared variables for CPU Load, Temperature, Fan RPMs, target fan speed, and target frequency.
- **UI Thread Decoupling**: Update the 250ms UI Timer tick to:
  - Read cached values and update stats labels/tray icon instantly.
  - Update the red crosshair tracking indicators on the charts without blocking.
  - Compute target fan speed and CPU frequency based on the current user-defined curves, writing the targets to background queue variables.

---

## Verification Plan

### Automated Tests
- Build the solution in Release mode using `dotnet build`.
- Publish the optimized standalone single-file binary.

### Manual Verification
- Launch the newly compiled application.
- Drag the dots on both charts back and forth at high speed. The dot movement must track the mouse pointer instantly and smoothly without any micro-stutters, freezing, or input lag.
- Verify that the target CPU frequency limits and fan speeds are still successfully applied in the background as the curves are changed.
