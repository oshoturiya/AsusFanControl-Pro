# 1. Terminate any running instances and delete scheduled task
Stop-Process -Name AsusFanControlGUI -Force -ErrorAction SilentlyContinue
schtasks.exe /delete /tn "AsusFanControlProStartup" /f

# 2. Restore Desktop Shortcut to point directly to the published EXE
$desktopPath = [System.IO.Path]::Combine([System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::UserProfile), "OneDrive", "Desktop")
if (-not (Test-Path $desktopPath)) {
    $desktopPath = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::Desktop)
}
$shortcutPath = [System.IO.Path]::Combine($desktopPath, "Asus Fan & CPU Controller Pro.lnk")
$exePath = "C:\Users\saksh\OneDrive\Documents\AsusFanControl-master\AsusFanControlGUI\bin\Release\net7.0-windows\win-x64\publish\AsusFanControlGUI.exe"
$exeDir = "C:\Users\saksh\OneDrive\Documents\AsusFanControl-master\AsusFanControlGUI\bin\Release\net7.0-windows\win-x64\publish"

$sh = New-Object -ComObject WScript.Shell
$sc = $sh.CreateShortcut($shortcutPath)
$sc.TargetPath = $exePath
$sc.WorkingDirectory = $exeDir
$sc.IconLocation = "$exePath,0"
$sc.Save()

Write-Output "Restored standard direct launch shortcut successfully!"
