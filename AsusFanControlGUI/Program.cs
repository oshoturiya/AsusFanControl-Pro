using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows.Forms;

namespace AsusFanControlGUI
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 0. Verify crucial driver dependency
            string driverPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AsusWinIO64.dll");
            if (!File.Exists(driverPath))
            {
                MessageBox.Show("Critical kernel driver 'AsusWinIO64.dll' was not found in the application directory.\n\nPlease extract all files into the same folder and try again.", "Missing Driver Dependency", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 1. First, ensure we are running as Administrator (either standard Admin or SYSTEM)
            if (!IsUserAnAdministrator() && !IsRunningAsSystem())
            {
                // Request standard UAC elevation
                try
                {
                    string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                    ProcessStartInfo uacPsi = new ProcessStartInfo
                    {
                        FileName = currentExe,
                        UseShellExecute = true,
                        Verb = "runas" // Standard UAC prompt!
                    };
                    Process.Start(uacPsi);
                    Application.Exit();
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("This application must be run as Administrator to control fans and power limits.\n\nError: " + ex.Message, "Elevation Required", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            // 2. We are Administrator. Check if PsExec is present in folder
            string currentFolder = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            string psexecPath = Path.Combine(currentFolder, "PsExec.exe");
            if (!File.Exists(psexecPath))
            {
                psexecPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PsExec.exe");
            }

            // 3. If PsExec is present, we follow the UAC-Bypass / SYSTEM path
            if (File.Exists(psexecPath))
            {
                if (IsRunningAsSystem())
                {
                    Application.Run(new Form1());
                }
                else
                {
                    // Self-healing scheduled task and desktop shortcut (UAC-Bypass)
                    HealScheduledTaskAndShortcut();
                    
                    // Relaunch as SYSTEM using PsExec
                    AttemptRelaunchAsSystem(psexecPath);
                }
            }
            else
            {
                // 4. Standalone/UAC Edition (PsExec missing)
                // We are already elevated as Administrator. Run directly!
                Application.Run(new Form1());
            }
        }

        static bool IsRunningAsSystem()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                return identity.IsSystem;
            }
        }

        static void AttemptRelaunchAsSystem(string psexecPath)
        {
            string currentExe = Process.GetCurrentProcess().MainModule.FileName;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = psexecPath;
                // -i: Interactive (show GUI)
                // -s: Run as System
                // -d: Don't wait for exit (detach)
                // -accepteula: Silently accept the license agreement
                psi.Arguments = $"-i -s -d -accepteula \"{currentExe}\"";
                psi.Verb = "runas"; // Request Admin for PsExec
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                Process.Start(psi);
                
                // Exit this instance so the SYSTEM instance can take over
                Application.Exit();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to launch as SYSTEM:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Run(new Form1());
            }
        }

        static bool IsUserAnAdministrator()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        static void HealScheduledTaskAndShortcut()
        {
            try
            {
                string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                string currentDir = Path.GetDirectoryName(currentExe);
                string taskName = "AsusFanControlProStartup";

                // Step 1. Check if task exists and points to current EXE
                bool taskOk = false;
                try
                {
                    var psiQuery = new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/query /xml /tn \"{taskName}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    using (var proc = Process.Start(psiQuery))
                    {
                        string xmlOut = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit();
                        if (proc.ExitCode == 0 && xmlOut.Contains(currentExe))
                        {
                            taskOk = true;
                        }
                    }
                }
                catch { }

                // Step 2. If task is not OK, register/update it
                if (!taskOk)
                {
                    string tempXmlPath = Path.Combine(currentDir, "task_heal_temp.xml");
                    string xmlContent = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <URI>\{taskName}</URI>
  </RegistrationInfo>
  <Principals>
    <Principal id=""Author"">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <IdleSettings>
      <Duration>PT10M</Duration>
      <WaitTimeout>PT1H</WaitTimeout>
      <StopOnIdleEnd>true</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
  </Settings>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
    </LogonTrigger>
  </Triggers>
  <Actions Context=""Author"">
    <Exec>
      <Command>""{currentExe}""</Command>
      <WorkingDirectory>{currentDir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
                    File.WriteAllText(tempXmlPath, xmlContent, System.Text.Encoding.Unicode);

                    var psiCreate = new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/create /tn \"{taskName}\" /xml \"{tempXmlPath}\" /f",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var proc = Process.Start(psiCreate))
                    {
                        proc.WaitForExit();
                    }

                    try { File.Delete(tempXmlPath); } catch { }
                }

                // Step 3. Heal/recreate Desktop Shortcut
                string desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Desktop");
                if (!Directory.Exists(desktopPath))
                {
                    desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                }
                string shortcutPath = Path.Combine(desktopPath, "Asus Fan & CPU Controller Pro.lnk");

                // Silent powershell shortcut creation
                string psCommand = $"$sh = New-Object -ComObject WScript.Shell; $sc = $sh.CreateShortcut('{shortcutPath}'); $sc.TargetPath = 'schtasks.exe'; $sc.Arguments = '/run /tn \"{taskName}\"'; $sc.IconLocation = '{currentExe},0'; $sc.Save()";
                var psiShortcut = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-WindowStyle Hidden -Command \"{psCommand}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psiShortcut))
                {
                    proc.WaitForExit();
                }
            }
            catch { }
        }
    }
}