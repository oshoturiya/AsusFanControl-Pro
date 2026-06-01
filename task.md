# Tasks

- [x] Declare Win32 Power Scheme P/Invoke APIs in `Form1.cs`
- [x] Create Background Hardware Worker Thread and thread-safe queues/caches in `Form1.cs`
- [x] Update `UpdateTimer_Tick` in `Form1.cs` to run lock-free on UI-cached variables
- [x] Build and verify successful C# compilation
- [x] Publish standalone single-file optimized executable
- [x] Recreate Desktop shortcut and verify smooth dragging manually
- [x] Locate winget-installed ThrottleStop files on the system
- [x] Copy ThrottleStop files to the project root and publish directories Side-by-Side
- [x] Register the scheduled UAC-bypass startup task using legacy schtasks.exe (fixing CimCmdlet hang)
- [x] Update task XML to set WorkingDirectory and disable battery power constraints
- [x] Verify both AsusFanControlGUI and ThrottleStop are running elevated side-by-side
- [x] Document ThrottleStop wrapper in AI_HANDOVER.md and update register_native_task.ps1
- [x] Implement robust startup dependency validation (AsusWinIO64.dll check)
- [x] Implement self-healing Scheduled Task check and auto-registration in C# code
- [x] Implement self-healing Desktop Shortcut generation in C# code
- [x] Successfully build and publish standalone, ReadyToRun single-file self-healing binary
- [x] Test and verify automated self-healing execution by deleting the task and shortcut, proving they recreate cleanly on startup
