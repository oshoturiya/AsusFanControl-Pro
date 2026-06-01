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

            // 1. Check if we are running as SYSTEM
            if (IsRunningAsSystem())
            {
                Application.Run(new Form1());
            }
            else
            {
                // Verify scheduled task and shortcut registration (Self-Healing)
                if (IsUserAnAdministrator())
                {
                    HealScheduledTaskAndShortcut();
                }

                // 2. Not System? Try to elevate
                AttemptRelaunchAsSystem();
            }
        }

        static bool IsRunningAsSystem()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                return identity.IsSystem;
            }
        }

        static void AttemptRelaunchAsSystem()
        {
            // Get the true location of the running .exe
            string currentExe = Process.GetCurrentProcess().MainModule.FileName;
            string currentFolder = Path.GetDirectoryName(currentExe);
            
            // Define where we look for PsExec
            string[] possiblePaths = {
                Path.Combine(currentFolder, "PsExec.exe"),                          // Same folder as EXE
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PsExec.exe"),  // App Base Directory
                Path.Combine(currentFolder, "..", "..", "..", "..", "PsExec.exe")   // Project Root (Debugging)
            };

            string psexecPath = null;
            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    psexecPath = path;
                    break;
                }
            }

            if (psexecPath == null)
            {
                MessageBox.Show($"PsExec.exe was not found.\n\nSearched in:\n{currentFolder}", 
                                "Missing Dependency", MessageBoxButtons.OK, MessageBoxIcon.Error);
                // Run anyway as Admin (fallback)
                Application.Run(new Form1());
                return;
            }

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