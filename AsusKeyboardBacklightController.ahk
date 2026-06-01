#Requires AutoHotkey v2.0
#SingleInstance Force

; ==============================================================================
; Asus Keyboard Backlight Controller (Bloat-Free Edition)
; ==============================================================================
; Hotkey: Right Alt (RAlt)
; Cycles through keyboard backlight levels: High (131) -> Med (130) -> Low (129) -> Off (128).
;
; Elevation and Startup Details:
; - Interfacing with the Asus WMI class requires Administrator privileges.
; - This script automatically elevates itself to Administrator on launch.
; - To run this silently at startup with Highest privileges (zero UAC prompts):
;   Open PowerShell as Administrator and execute:
;
;   schtasks /create /tn "AsusKeyboardBacklightController" /tr "powershell -WindowStyle Hidden -Command Start-Process -FilePath 'C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe' -ArgumentList 'C:\Users\saksh\OneDrive\Documents\AsusFanControl-master\AsusKeyboardBacklightController.ahk'" /sc onlogon /rl highest /f
; ==============================================================================

; Elevate script to Administrator if not already running elevated
if not A_IsAdmin {
    try Run "*RunAs `"" A_ScriptFullPath "`""
    ExitApp
}

Global CurrentCode := 131

; Map Right Alt to trigger the cycle function
RAlt::CycleAsusLight()

; Map Ctrl + Shift + Right Alt to perform a clean, deep Windows shutdown
^+RAlt::Run("shutdown.exe /s /f /t 0")

CycleAsusLight() {
    Global CurrentCode
    
    ; Decrement state
    CurrentCode := CurrentCode - 1
    
    ; Reset loop if below Off (128)
    if (CurrentCode < 128) {
        CurrentCode := 131 
    }

    ; Human-readable labels
    if (CurrentCode = 128)
        StateText := "OFF"
    else if (CurrentCode = 129)
        StateText := "Low"
    else if (CurrentCode = 130)
        StateText := "Med"
    else
        StateText := "High"

    ; Display temporary tooltip
    ToolTip("Keyboard Backlight: " . StateText)
    SetTimer () => ToolTip(), -1000

    ; Execute WMI method to control backlight (Device ID: 0x00050021)
    try {
        psCmd := "Get-CimInstance -Namespace root/wmi -ClassName AsusAtkWmi_WMNB | Invoke-CimMethod -MethodName DEVS -Arguments @{Device_ID=0x00050021; Control_status=[int]" . CurrentCode . "}"
        Run("powershell.exe -NoProfile -WindowStyle Hidden -Command `"" . psCmd . "`"",, "Hide")
    }
}
