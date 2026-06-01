# Kill any existing background instances
Stop-Process -Name AsusFanControlGUI -Force -ErrorAction SilentlyContinue

$exePath = "C:\Users\saksh\OneDrive\Documents\AsusFanControl-master\AsusFanControlGUI\bin\Release\net7.0-windows\win-x64\publish\AsusFanControlGUI.exe"
$exeDir = "C:\Users\saksh\OneDrive\Documents\AsusFanControl-master\AsusFanControlGUI\bin\Release\net7.0-windows\win-x64\publish"
$taskName = "AsusFanControlProStartup"

# Register the task cleanly and instantly using the legacy schtasks.exe (immune to CimCmdlet hanging)
schtasks.exe /create /tn $taskName /tr "`"$exePath`"" /sc ONLOGON /rl HIGHEST /f

# Export, update working directory, and disable battery constraints in XML
$xmlStr = [string](schtasks.exe /query /xml /tn $taskName)
$xml = [xml]$xmlStr
$ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
$ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task")

# 1. Inject WorkingDirectory
$execNode = $xml.SelectSingleNode("//t:Exec", $ns)
$wdNode = $xml.CreateElement("WorkingDirectory", "http://schemas.microsoft.com/windows/2004/02/mit/task")
$wdNode.InnerText = $exeDir
$execNode.AppendChild($wdNode) | Out-Null

# 2. Disable battery limitations (allow starting and continuing on battery)
$settingsNode = $xml.SelectSingleNode("//t:Settings", $ns)
$disallowNode = $settingsNode.SelectSingleNode("t:DisallowStartIfOnBatteries", $ns)
if ($disallowNode) { $disallowNode.InnerText = "false" }
$stopNode = $settingsNode.SelectSingleNode("t:StopIfGoingOnBatteries", $ns)
if ($stopNode) { $stopNode.InnerText = "false" }

# Save updated XML and re-import
$xmlTempPath = [System.IO.Path]::Combine($exeDir, "task_updated_temp.xml")
$xml.Save($xmlTempPath)
schtasks.exe /create /tn $taskName /xml $xmlTempPath /f
Remove-Item $xmlTempPath -Force

# Recreate Desktop shortcut to target the task scheduler run command
$desktopPath = [System.IO.Path]::Combine([System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::UserProfile), "OneDrive", "Desktop")
if (-not (Test-Path $desktopPath)) {
    $desktopPath = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::Desktop)
}
$shortcutPath = [System.IO.Path]::Combine($desktopPath, "Asus Fan & CPU Controller Pro.lnk")

$sh = New-Object -ComObject WScript.Shell
$sc = $sh.CreateShortcut($shortcutPath)
$sc.TargetPath = "schtasks.exe"
$sc.Arguments = "/run /tn `"$taskName`""
$sc.IconLocation = "$exePath,0"
$sc.Save()

# Try running the task manually to test if it starts visible
schtasks.exe /run /tn $taskName

Write-Output "Native task registered, shortcut updated, and task launched successfully!"
