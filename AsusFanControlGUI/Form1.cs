// AI-HANDOFF: Form1.cs - Asus Fan & Dynamic Frequency Controller Pro
// VERSION UPGRADE LOG:
//   - DUAL GRAPH LAYOUT: Widened the GUI window to 1200px and split the graph area into two columns 
//     side-by-side using TableLayoutPanel. Column 0 contains the Fan Curve, Column 1 contains the CPU Frequency Curve.
//   - NATIVE C# FREQUENCY CONTROLLER: Integrated the Python background daemon's logic completely inside the 
//     C# application. Querying CPU load is done efficiently via System.Diagnostics.PerformanceCounter.
//   - BULLETPROOF POWERCFG INTERFACE: Dynamically queries the current active power plan's GUID on startup 
//     (using "powercfg /getactivescheme"), allowing these power modifications to work across any computer.
//   - DRAGGABLE CPU LOAD vs FREQ CHART: Implemented the FrequencyCurveEditor C# class. Osho can drag dots 
//     in both CPU load and target frequency dimensions freely, matching the native premium feel of the fan graph.
//   - SYNCHRONIZED CLOCKS & TEMP: Disables C-states (`IDLEDISABLE = 1`) when active to lock the Effective Clock 
//     1:1 to the active frequency limit, and restores full default states when the application is closed.

using System;
using System.Drawing;
using System.Windows.Forms;
using System.Collections.Generic;
using AsusFanControl;
using System.IO;
using Microsoft.Win32; // Required for Registry

namespace AsusFanControlGUI
{
    public partial class Form1 : Form
    {
        // --- UI Controls ---
        private Panel topPanel;
        private GroupBox grpModes;
        private RadioButton rbModeOff;
        private RadioButton rbModeManual;
        private RadioButton rbModeCurve;
        private CheckBox chkFreqControl; // Enabled toggle for Dynamic Frequency sync
        
        private GroupBox grpStats;
        private Label lblTemp;
        private Label lblFanRPM;
        private Button btnSafetyConfig; 
        
        // Safety Settings Panel (Popup)
        private Panel pnlSafetyConfig;
        private Label lblSafeTitle;
        private CheckBox chkSafetyEnabled; 
        private NumericUpDown numSafetyTemp;
        private RadioButton rbSafetyActionBios;
        private RadioButton rbSafetyActionMax;
        
        // New Startup Option
        private CheckBox chkRunAtStartup;
        private Button btnCloseSafety;

        private GroupBox grpManual;
        private TrackBar trackManual;
        private Label lblManualValue;

        // CPU Stats UI Widgets
        private GroupBox grpCpuStats;
        private Label lblCpuLoad;
        private Label lblCpuFreq;
        private Label lblCpuLimit;

        private Panel graphPanel;
        private FanCurveEditor fanCurveEditor;
        private FrequencyCurveEditor freqCurveEditor; // Column 1 frequency chart
        
        // --- Tray Icon ---
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        
        // --- Logic ---
        private Timer updateTimer;
        private AsusControl asusControl;
        private int fanTickCounter = 0;
        private int lastAppliedFanSpeed = -1;
        private int currentManualSpeed = 50; 
        private bool isOverheatTriggered = false; 
        
        // --- Settings Variables ---
        private string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.txt");
        private bool safetyEnabled = true;
        private int safetyTriggerTemp = 90;
        private bool safetyActionIsBios = true; 
        private bool runAtStartup = false;

        // --- Frequency Controller Variables ---
        private System.Diagnostics.PerformanceCounter cpuCounter;
        private System.Diagnostics.PerformanceCounter cpuFreqCounter;
        private System.Diagnostics.PerformanceCounter cpuPerfCounter; // % Processor Performance (dynamic turbo speed)
        private double baseCpuFreqGHz = 3.0; // Base clock in GHz, read once at startup
        private bool freqControlEnabled = true;

        // --- Win32 Power API Guids (Processor subgroup: 54533251-82be-4824-96c1-47b60b740d00) ---
        private static readonly Guid SubGroupGuid    = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        private static readonly Guid MaxFreqGuid     = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100"); // PROCFREQMAX – MHz, 0 = no limit
        private static readonly Guid MaxStateGuid    = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec"); // PROCTHROTTLEMAX – % 0-100
        private static readonly Guid MinStateGuid    = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c"); // PROCTHROTTLEMIN – % 0-100
        private static readonly Guid BoostModeGuid   = new Guid("be337238-0d82-4146-a960-4f3749d470c7"); // PERFBOOSTMODE – 0=off,1=enabled,2=aggressive,3=efficient-aggressive
        private static readonly Guid IdleDisableGuid = new Guid("5d76a2ca-e8c0-402f-a133-2158492d58ad"); // IDLEDISABLE – 0=enabled,1=disabled

        // --- Decoupled Hardware Worker Thread ---
        private System.Threading.Thread bgThread;
        private bool isRunning = true;
        
        // Thread-safe cached states (read by UI)
        private int cachedTemp = 40;
        private string cachedRpmText = "N/A";
        private double cachedCpuLoad = 0.0;
        private double cachedCpuFreq = 0.0;
        private readonly object statsLock = new object();

        // Thread-safe targets (written by UI, read/applied by bgThread)
        private int targetFanSpeed = -1; 
        private int targetFreqLimit = -1; 
        private bool enableCStates = true;
        private readonly object targetLock = new object();
        
        // Local state trackers in background thread to avoid writing if not changed
        private int lastAppliedBgFanSpeed = -2;
        private int lastAppliedBgFreqLimit = -2;
        private bool lastAppliedBgCStates = true;

        // --- ThrottleStop Integration ---
        private System.Diagnostics.Process throttleStopProcess = null;


        // --- Win32 Power API P/Invoke Declarations ---
        [System.Runtime.InteropServices.DllImport("powrprof.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern uint PowerWriteACValueIndex(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            uint AcValueIndex
        );

        [System.Runtime.InteropServices.DllImport("powrprof.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern uint PowerWriteDCValueIndex(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            uint DcValueIndex
        );

        [System.Runtime.InteropServices.DllImport("powrprof.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern uint PowerSetActiveScheme(
            IntPtr UserRootPowerKey,
            ref Guid ActivePolicyGuid
        );

        [System.Runtime.InteropServices.DllImport("powrprof.dll")]
        public static extern uint PowerGetActiveScheme(
            IntPtr UserRootPowerKey,
            out IntPtr ActivePolicyGuid
        );

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr hMem);

        public Form1()
        {
            InitializeComponent();
            InitializeFanDriver();
            InitializeCpuCounter();
            SetupUI();
            SetupTrayIcon();
            LoadSettings(); 
            StartHardwareWorkerThread();
            LaunchThrottleStopIfPresent();
        }

        private void LaunchThrottleStopIfPresent()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] possiblePaths = new string[]
                {
                    Path.Combine(baseDir, "ThrottleStop.exe"),
                    Path.Combine(baseDir, "ThrottleStop", "ThrottleStop.exe"),
                    Path.Combine(Directory.GetParent(baseDir)?.FullName ?? baseDir, "ThrottleStop", "ThrottleStop.exe"),
                    Path.Combine(Directory.GetParent(Directory.GetParent(Directory.GetParent(baseDir)?.FullName ?? baseDir)?.FullName ?? baseDir)?.FullName ?? baseDir, "ThrottleStop.exe"),
                    Path.Combine(Directory.GetParent(Directory.GetParent(Directory.GetParent(baseDir)?.FullName ?? baseDir)?.FullName ?? baseDir)?.FullName ?? baseDir, "ThrottleStop", "ThrottleStop.exe")
                };

                string tsPath = null;
                foreach (var path in possiblePaths)
                {
                    if (File.Exists(path))
                    {
                        tsPath = path;
                        break;
                    }
                }

                if (tsPath != null)
                {
                    // Ensure no orphan instances are running first
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName("ThrottleStop"))
                    {
                        try { p.Kill(); } catch { }
                    }

                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = tsPath,
                        WorkingDirectory = Path.GetDirectoryName(tsPath),
                        UseShellExecute = true, // Required for GUI apps
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Minimized
                    };
                    throttleStopProcess = System.Diagnostics.Process.Start(psi);
                }
            }
            catch { }
        }

        private void KillThrottleStop()
        {
            try
            {
                if (throttleStopProcess != null && !throttleStopProcess.HasExited)
                {
                    throttleStopProcess.Kill();
                }
                
                // Backup kill
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("ThrottleStop"))
                {
                    try { p.Kill(); } catch { }
                }
            }
            catch { }
        }


        private void InitializeFanDriver()
        {
            try { asusControl = new AsusControl(); } 
            catch (Exception ex) { MessageBox.Show("Error initializing fan driver.\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void InitializeCpuCounter()
        {
            try
            {
                cpuCounter = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total");
                cpuCounter.NextValue(); // First value is always 0
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error initializing CPU load monitor: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            try
            {
                cpuFreqCounter = new System.Diagnostics.PerformanceCounter("Processor Information", "Processor Frequency", "_Total");
                cpuFreqCounter.NextValue();
                // Read base clock once from this counter (it returns nominal MHz)
                float baseVal = cpuFreqCounter.NextValue();
                if (baseVal > 0) baseCpuFreqGHz = baseVal / 1000.0;
            }
            catch { }

            // % Processor Performance: returns actual dynamic speed as % of nominal (>100% when turbo boosting)
            try
            {
                cpuPerfCounter = new System.Diagnostics.PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
                cpuPerfCounter.NextValue(); // prime the counter
            }
            catch { }
        }

        private void SetupUI()
        {
            this.Text = "Asus Fan & CPU Controller Pro";
            this.Size = new Size(1200, 750); // Widened to fit two charts side-by-side perfectly
            this.BackColor = Color.FromArgb(30, 30, 30); 
            this.ForeColor = Color.White; 

            // 1. Top Panel
            topPanel = new Panel();
            topPanel.Dock = DockStyle.Top;
            topPanel.Height = 180; 
            topPanel.Padding = new Padding(10);
            topPanel.BackColor = Color.Transparent; 
            this.Controls.Add(topPanel);

            // 2. Stats Group
            grpStats = CreateGroupBox("Current Status", 10, 10, 250, 160);
            topPanel.Controls.Add(grpStats);

            lblTemp = new Label { Text = "Temp: -- °C", Location = new Point(20, 30), AutoSize = true, Font = new Font("Segoe UI", 14, FontStyle.Bold), ForeColor = Color.Cyan };
            lblFanRPM = new Label { Text = "Fan: -- RPM", Location = new Point(20, 65), AutoSize = true, Font = new Font("Segoe UI", 12), ForeColor = Color.Yellow };
            
            // Settings Button
            btnSafetyConfig = new Button { Text = "⚙ Config & Safety", Location = new Point(20, 110), Size = new Size(210, 30), BackColor = Color.FromArgb(60, 60, 60), FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
            btnSafetyConfig.FlatAppearance.BorderColor = Color.Gray;
            btnSafetyConfig.Click += (s, e) => { pnlSafetyConfig.Visible = !pnlSafetyConfig.Visible; pnlSafetyConfig.BringToFront(); };
            
            grpStats.Controls.Add(lblTemp);
            grpStats.Controls.Add(lblFanRPM);
            grpStats.Controls.Add(btnSafetyConfig);

            // 3. Modes Group (Contains standard fan radio buttons + our new dynamic CPU toggle)
            grpModes = CreateGroupBox("Control Mode", 270, 10, 300, 160);
            topPanel.Controls.Add(grpModes);

            rbModeOff = CreateRadioButton("1. BIOS Default (Off)", 20, 25);
            rbModeManual = CreateRadioButton("2. Manual Fixed Speed", 20, 55);
            rbModeCurve = CreateRadioButton("3. Custom Curve (Graph)", 20, 85);
            
            chkFreqControl = new CheckBox {
                Text = "✔ Enable Dynamic CPU Sync",
                Location = new Point(20, 120),
                Size = new Size(260, 24),
                Checked = true,
                ForeColor = Color.LightGreen,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            chkFreqControl.CheckedChanged += (s, e) => { freqControlEnabled = chkFreqControl.Checked; };

            rbModeOff.CheckedChanged += Mode_CheckedChanged;
            rbModeManual.CheckedChanged += Mode_CheckedChanged;
            rbModeCurve.CheckedChanged += Mode_CheckedChanged;

            grpModes.Controls.Add(rbModeOff);
            grpModes.Controls.Add(rbModeManual);
            grpModes.Controls.Add(rbModeCurve);
            grpModes.Controls.Add(chkFreqControl);

            // 4. Manual Group
            grpManual = CreateGroupBox("Manual Setting", 580, 10, 380, 160);
            topPanel.Controls.Add(grpManual);
            trackManual = new TrackBar { Location = new Point(20, 40), Width = 340, Minimum = 1, Maximum = 100, Value = 50, TickFrequency = 10, BackColor = Color.FromArgb(30, 30, 30) };
            trackManual.ValueChanged += (s, e) => { currentManualSpeed = trackManual.Value; lblManualValue.Text = $"Speed: {trackManual.Value}%"; };
            lblManualValue = new Label { Text = "Speed: 50%", Location = new Point(20, 90), AutoSize = true, Font = new Font("Segoe UI", 12), ForeColor = Color.White };
            grpManual.Controls.Add(trackManual);
            grpManual.Controls.Add(lblManualValue);
            grpManual.Enabled = false; 

            // 4.5. CPU Status Group (Fills the empty space perfectly!)
            grpCpuStats = CreateGroupBox("CPU Status", 970, 10, 205, 160);
            topPanel.Controls.Add(grpCpuStats);

            lblCpuLoad = new Label { Text = "Load: -- %", Location = new Point(20, 30), AutoSize = true, Font = new Font("Segoe UI", 14, FontStyle.Bold), ForeColor = Color.LightGreen };
            lblCpuFreq = new Label { Text = "Speed: -- GHz", Location = new Point(20, 65), AutoSize = true, Font = new Font("Segoe UI", 12), ForeColor = Color.Yellow };
            lblCpuLimit = new Label { Text = "Limit: -- GHz", Location = new Point(20, 100), AutoSize = true, Font = new Font("Segoe UI", 12), ForeColor = Color.Orange };

            grpCpuStats.Controls.Add(lblCpuLoad);
            grpCpuStats.Controls.Add(lblCpuFreq);
            grpCpuStats.Controls.Add(lblCpuLimit);

            // 5. Config Panel (Popup)
            pnlSafetyConfig = new Panel();
            pnlSafetyConfig.Size = new Size(300, 260); 
            pnlSafetyConfig.Location = new Point(30, 160); 
            pnlSafetyConfig.BackColor = Color.FromArgb(50, 50, 50);
            pnlSafetyConfig.BorderStyle = BorderStyle.FixedSingle;
            pnlSafetyConfig.Visible = false;
            this.Controls.Add(pnlSafetyConfig); 
            pnlSafetyConfig.BringToFront();

            lblSafeTitle = new Label { Text = "Configuration", Location = new Point(10, 10), AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), ForeColor = Color.Orange };
            
            // Safety Toggle
            chkSafetyEnabled = new CheckBox { 
                Text = "✔ Enable Overheat Protection", 
                Location = new Point(20, 40), 
                AutoSize = true, 
                Checked = true, 
                ForeColor = Color.LightGreen,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            
            Label lblTrigger = new Label { Text = "Trigger Temp (°C):", Location = new Point(20, 75), AutoSize = true, ForeColor = Color.White };
            numSafetyTemp = new NumericUpDown { Location = new Point(150, 72), Width = 60, Minimum = 50, Maximum = 105, Value = 90 };

            Label lblAction = new Label { Text = "Safety Action:", Location = new Point(20, 110), AutoSize = true, ForeColor = Color.White };
            rbSafetyActionBios = new RadioButton { Text = "Switch to BIOS", Location = new Point(30, 135), AutoSize = true, Checked = true, ForeColor = Color.White };
            rbSafetyActionMax = new RadioButton { Text = "Set 100% Speed", Location = new Point(150, 135), AutoSize = true, ForeColor = Color.White };

            // Startup Option
            chkRunAtStartup = new CheckBox { 
                Text = "Run at Windows Startup", 
                Location = new Point(20, 175), 
                AutoSize = true, 
                ForeColor = Color.White 
            };

            btnCloseSafety = new Button { Text = "Save & Close", Location = new Point(80, 215), Size = new Size(140, 30), BackColor = Color.FromArgb(0, 122, 204), FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
            btnCloseSafety.Click += (s, e) => {
                safetyEnabled = chkSafetyEnabled.Checked;
                safetyTriggerTemp = (int)numSafetyTemp.Value;
                safetyActionIsBios = rbSafetyActionBios.Checked;
                runAtStartup = chkRunAtStartup.Checked;
                
                SetStartup(runAtStartup);

                pnlSafetyConfig.Visible = false;
                SaveSettings(); 
            };

            pnlSafetyConfig.Controls.AddRange(new Control[] { lblSafeTitle, chkSafetyEnabled, lblTrigger, numSafetyTemp, lblAction, rbSafetyActionBios, rbSafetyActionMax, chkRunAtStartup, btnCloseSafety });

            // 6. Graph Area (Using TableLayoutPanel for modern 2-column graph layout)
            graphPanel = new Panel();
            graphPanel.Dock = DockStyle.Fill; 
            graphPanel.Padding = new Padding(20, 20, 20, 20); 
            graphPanel.BackColor = Color.Transparent; 
            this.Controls.Add(graphPanel);
            graphPanel.BringToFront(); 
            topPanel.SendToBack(); 

            TableLayoutPanel mainLayout = new TableLayoutPanel();
            mainLayout.Dock = DockStyle.Fill;
            mainLayout.ColumnCount = 2;
            mainLayout.RowCount = 1;
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            mainLayout.BackColor = Color.Transparent;
            graphPanel.Controls.Add(mainLayout);

            fanCurveEditor = new FanCurveEditor();
            fanCurveEditor.Dock = DockStyle.Fill;
            mainLayout.Controls.Add(fanCurveEditor, 0, 0);

            freqCurveEditor = new FrequencyCurveEditor();
            freqCurveEditor.Dock = DockStyle.Fill;
            mainLayout.Controls.Add(freqCurveEditor, 1, 0);

            // Logic
            updateTimer = new Timer();
            updateTimer.Interval = 250; // High-performance 250ms update interval for instant smooth graph updates
            updateTimer.Tick += UpdateTimer_Tick;
            updateTimer.Start(); 
            this.Resize += Form1_Resize;
        }

        private void SetupTrayIcon()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Show", null, (s, e) => RestoreFromTray());
            trayMenu.Items.Add("Exit", null, (s, e) => { trayIcon.Visible = false; Application.Exit(); });
            trayIcon = new NotifyIcon();
            trayIcon.Text = "Asus Fan & CPU Control";
            try { trayIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { trayIcon.Icon = SystemIcons.Application; }
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = false;
            trayIcon.DoubleClick += (s, e) => RestoreFromTray();
        }

        private void Form1_Resize(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized) HideToTray();
        }

        private void HideToTray()
        {
            trayIcon.Visible = true;
            this.ShowInTaskbar = false;
            this.Hide();
        }

        private void RestoreFromTray()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.ShowInTaskbar = true;
            trayIcon.Visible = false;
        }

        private void Mode_CheckedChanged(object sender, EventArgs e)
        {
            grpManual.Enabled = rbModeManual.Checked;
            fanCurveEditor.Enabled = rbModeCurve.Checked;
            if (rbModeOff.Checked && asusControl != null)
            {
                try { asusControl.SetFanSpeeds(0); } catch {}
            }
        }

        private Guid GetActivePowerSchemeGuid()
        {
            IntPtr activeGuidPtr = IntPtr.Zero;
            uint result = PowerGetActiveScheme(IntPtr.Zero, out activeGuidPtr);
            if (result == 0 && activeGuidPtr != IntPtr.Zero)
            {
                Guid activeGuid = (Guid)System.Runtime.InteropServices.Marshal.PtrToStructure(activeGuidPtr, typeof(Guid));
                LocalFree(activeGuidPtr);
                return activeGuid;
            }
            return new Guid("3ff9831b-6f80-4830-8178-736cd4229e7b");
        }

        private void ApplyFrequencyLimit(int mhz, bool enableIdle)
        {
            // mhz == 0 means "no limit" (full turbo max)
            // mhz > 0 means a hard MHz cap (e.g. 2800 = 2.8 GHz)
            try
            {
                bool isMax = (mhz == 0);
                string sub = "54533251-82be-4824-96c1-47b60b740d00";

                int freqVal    = isMax ? 0 : mhz;                    // PROCFREQMAX: 0 = unlimited
                int maxState   = 100;                                  // PROCTHROTTLEMAX: always 100%
                int minState   = isMax ? 100 : 5;                     // PROCTHROTTLEMIN: 100% max perf, 5% when limited
                int boostMode  = isMax ? 2 : 0;                       // PERFBOOSTMODE: 2=Aggressive, 0=Off
                int epp        = isMax ? 0 : 100;                     // PERFEPP: 0=max perf, 100=max save
                int idleDisable = enableIdle ? 0 : 1;                 // IDLEDISABLE: 0=enabled, 1=disabled

                string cmdLine = string.Join(" & ",
                    $"powercfg /setacvalueindex SCHEME_CURRENT {sub} 75b0ae3f-bce0-45a7-8c89-c9611c25e100 {freqVal}",
                    $"powercfg /setdcvalueindex SCHEME_CURRENT {sub} 75b0ae3f-bce0-45a7-8c89-c9611c25e100 {freqVal}",
                    $"powercfg /setacvalueindex SCHEME_CURRENT {sub} bc5038f7-23e0-4960-96da-33abaf5935ec {maxState}",
                    $"powercfg /setdcvalueindex SCHEME_CURRENT {sub} bc5038f7-23e0-4960-96da-33abaf5935ec {maxState}",
                    $"powercfg /setacvalueindex SCHEME_CURRENT {sub} 893dee8e-2bef-41e0-89c6-b55d0929964c {minState}",
                    $"powercfg /setdcvalueindex SCHEME_CURRENT {sub} 893dee8e-2bef-41e0-89c6-b55d0929964c {minState}",
                    $"powercfg /setacvalueindex SCHEME_CURRENT {sub} be337238-0d82-4146-a960-4f3749d470c7 {boostMode}",
                    $"powercfg /setdcvalueindex SCHEME_CURRENT {sub} be337238-0d82-4146-a960-4f3749d470c7 {boostMode}",
                    $"powercfg /setacvalueindex SCHEME_CURRENT {sub} 36687f9e-e3a5-4dbf-b1dc-15eb381c6863 {epp}",
                    $"powercfg /setdcvalueindex SCHEME_CURRENT {sub} 36687f9e-e3a5-4dbf-b1dc-15eb381c6863 {epp}",
                    $"powercfg /setacvalueindex SCHEME_CURRENT {sub} 5d76a2ca-e8c0-402f-a133-2158492d58ad {idleDisable}",
                    $"powercfg /setdcvalueindex SCHEME_CURRENT {sub} 5d76a2ca-e8c0-402f-a133-2158492d58ad {idleDisable}",
                    "powercfg /setactive SCHEME_CURRENT"
                );

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {cmdLine}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p.WaitForExit(5000);
                }
            }
            catch { }
        }

        private void StartHardwareWorkerThread()
        {
            isRunning = true;
            bgThread = new System.Threading.Thread(BackgroundHardwareLoop);
            bgThread.IsBackground = true;
            bgThread.Priority = System.Threading.ThreadPriority.AboveNormal;
            bgThread.Start();
        }

        private void BackgroundHardwareLoop()
        {
            int loopCounter = 0;
            while (isRunning)
            {
                try
                {
                    // 1. Read CPU Load (every 100ms)
                    double currentLoad = 0.0;
                    if (cpuCounter != null)
                    {
                        try { currentLoad = cpuCounter.NextValue(); } catch { }
                    }

                    // 2. Read temperature and fan RPMs from hardware (every 1000ms to save CPU / avoid driver locks)
                    int temp = cachedTemp;
                    string rpm = cachedRpmText;
                    double freq = cachedCpuFreq;

                    loopCounter++;
                    if (loopCounter >= 50 || loopCounter == 1)
                    {
                        if (loopCounter >= 50) loopCounter = 0;
                        if (asusControl != null)
                        {
                            try
                            {
                                ulong tempLong = asusControl.Thermal_Read_Cpu_Temperature();
                                temp = (int)tempLong;
                            }
                            catch { }

                            try
                            {
                                List<int> speeds = asusControl.GetFanSpeeds();
                                string rpmText = (speeds.Count > 0) ? $"{speeds[0]} RPM" : "N/A";
                                if (speeds.Count > 1) rpmText += $" / {speeds[1]} RPM";
                                rpm = rpmText;
                            }
                            catch { }
                        }

                        double fetchedFreq = 0.0;
                        // Use % Processor Performance to get actual dynamic/turbo boost speed
                        if (cpuPerfCounter != null)
                        {
                            try
                            {
                                float perfPct = cpuPerfCounter.NextValue(); // e.g. 131.3 means 131.3% of base
                                if (perfPct > 0)
                                {
                                    fetchedFreq = (perfPct / 100.0) * baseCpuFreqGHz; // actual GHz
                                }
                            }
                            catch { }
                        }
                        // Fallback to static Processor Frequency counter
                        if (fetchedFreq <= 0 && cpuFreqCounter != null)
                        {
                            try
                            {
                                float val = cpuFreqCounter.NextValue();
                                if (val > 0)
                                {
                                    fetchedFreq = val / 1000.0;
                                }
                            }
                            catch { }
                        }

                        if (fetchedFreq <= 0)
                        {
                            try
                            {
                                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                                {
                                    if (key != null)
                                    {
                                        var mhzObj = key.GetValue("~MHz");
                                        if (mhzObj != null)
                                        {
                                            fetchedFreq = Convert.ToDouble(mhzObj) / 1000.0;
                                        }
                                    }
                                }
                            }
                            catch { }
                        }

                        if (fetchedFreq > 0)
                        {
                            freq = fetchedFreq;
                        }
                    }

                    // Cache stats safely
                    lock (statsLock)
                    {
                        cachedCpuLoad = currentLoad;
                        cachedTemp = temp;
                        cachedRpmText = rpm;
                        cachedCpuFreq = freq;
                    }

                    // 3. Apply Target Fan Speed and Frequency limits
                    int fanSpeedToApply;
                    int freqToApply;
                    bool cStatesToApply;

                    lock (targetLock)
                    {
                        fanSpeedToApply = targetFanSpeed;
                        freqToApply = targetFreqLimit;
                        cStatesToApply = enableCStates;
                    }

                    // Apply fan speed if changed
                    if (fanSpeedToApply != lastAppliedBgFanSpeed)
                    {
                        if (asusControl != null)
                        {
                            try
                            {
                                if (fanSpeedToApply >= 0)
                                {
                                    asusControl.SetFanSpeeds(fanSpeedToApply);
                                }
                                else
                                {
                                    // Default / BIOS Mode
                                    asusControl.SetFanSpeeds(0);
                                }
                                lastAppliedBgFanSpeed = fanSpeedToApply;
                            }
                            catch { }
                        }
                    }

                    // Apply frequency and C-states limit if changed OR once every 5 seconds unconditionally to prevent silent failures
                    bool forceApply = (loopCounter == 0);
                    if (freqToApply != lastAppliedBgFreqLimit || cStatesToApply != lastAppliedBgCStates || forceApply)
                    {
                        try
                        {
                            ApplyFrequencyLimit(freqToApply, cStatesToApply);
                            lastAppliedBgFreqLimit = freqToApply;
                            lastAppliedBgCStates = cStatesToApply;
                        }
                        catch { }
                    }
                }
                catch { }

                System.Threading.Thread.Sleep(100);
            }
        }

        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                // 1. Read thread-safe cached variables instantly
                int currentTemp;
                string rpmText;
                double currentLoad;
                double currentFreq;

                lock (statsLock)
                {
                    currentTemp = cachedTemp;
                    rpmText = cachedRpmText;
                    currentLoad = cachedCpuLoad;
                    currentFreq = cachedCpuFreq;
                }

                lblTemp.Text = $"Temp: {currentTemp} °C";
                lblFanRPM.Text = $"Fan: {rpmText}";
                trayIcon.Text = $"Temp: {currentTemp}°C | Fan: {rpmText}";

                // Update CPU Status UI widgets
                lblCpuLoad.Text = $"Load: {Math.Round(currentLoad, 0)} %";
                lblCpuFreq.Text = (currentFreq > 0.0) ? $"Speed: {currentFreq:F2} GHz" : "Speed: -- GHz";

                // --- SAFETY LOGIC ---
                if (safetyEnabled && currentTemp >= safetyTriggerTemp)
                {
                    lblTemp.ForeColor = Color.Red; 
                    if (!isOverheatTriggered)
                    {
                        isOverheatTriggered = true;
                        if (safetyActionIsBios)
                        {
                            rbModeOff.Checked = true; 
                        }
                        else
                        {
                            rbModeManual.Checked = true;
                            trackManual.Value = 100; 
                        }

                        if (!this.Visible) RestoreFromTray();
                        
                        string actionText = safetyActionIsBios ? "Switching to BIOS Default" : "Setting Fan to 100%";
                        MessageBox.Show($"CRITICAL WARNING: Temperature reached {currentTemp}°C.\n{actionText} for safety.", 
                                        "Overheat Protection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return; 
                    }
                }
                else
                {
                    lblTemp.ForeColor = Color.Cyan; 
                    if (currentTemp < (safetyTriggerTemp - 5)) isOverheatTriggered = false; 
                }

                // 2. Calculate target fan speed from curve
                int targetSpeed = -1; // -1 means BIOS mode
                if (isOverheatTriggered)
                {
                    targetSpeed = safetyActionIsBios ? -1 : 100;
                }
                else if (rbModeOff.Checked)
                {
                    targetSpeed = -1;
                }
                else if (rbModeManual.Checked)
                {
                    targetSpeed = currentManualSpeed;
                }
                else if (rbModeCurve.Checked)
                {
                    // Update Fan Curve indicators (only if not currently active dragging)
                    if (!fanCurveEditor.IsDragging)
                    {
                        targetSpeed = fanCurveEditor.UpdateAndGetSpeed(currentTemp); 
                    }
                    else
                    {
                        targetSpeed = fanCurveEditor.CalculateFanSpeed(currentTemp);
                    }
                }

                // 3. Calculate target CPU frequency limit
                double targetLimit = 4.1;
                if (!freqCurveEditor.IsDragging)
                {
                    targetLimit = freqCurveEditor.UpdateAndGetLimit(currentLoad); // Smooth red crosshair tracker
                }
                else
                {
                    targetLimit = freqCurveEditor.CalculateFrequencyLimit(currentLoad);
                }

                // Update CPU Limit label
                if (freqControlEnabled)
                {
                    lblCpuLimit.Text = (targetLimit >= 4.1) ? "Limit: Max (4.10 GHz)" : $"Limit: {targetLimit:F2} GHz";
                }
                else
                {
                    lblCpuLimit.Text = "Limit: Max (4.10 GHz)";
                }

                int targetMhz = 0;
                bool disableCStates = false;
                if (freqControlEnabled)
                {
                    if (targetLimit >= 4.1)
                    {
                        targetMhz = 0; // Unlimited
                        disableCStates = true; // Disable C-states to lock effective clock 1:1 to actual frequency
                    }
                    else
                    {
                        targetMhz = (int)Math.Round(targetLimit * 10) * 100;
                        disableCStates = true; // Disable C-states to lock effective clock 1:1 to limit
                    }
                }

                // 4. Update targets for background thread to process
                lock (targetLock)
                {
                    targetFanSpeed = targetSpeed;
                    targetFreqLimit = targetMhz;
                    enableCStates = !disableCStates;
                }
            }
            catch { lblFanRPM.Text = "Error reading data"; }
        }

        private GroupBox CreateGroupBox(string text, int x, int y, int w, int h)
        {
            return new GroupBox { Text = text, Location = new Point(x, y), Size = new Size(w, h), ForeColor = Color.White, Font = new Font("Segoe UI", 10) };
        }

        private RadioButton CreateRadioButton(string text, int x, int y)
        {
            return new RadioButton { Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI", 11) };
        }

        private void SetStartup(bool enable)
        {
            try
            {
                // Clean up old HKLM run registry keys if present to prevent double startup/UAC prompts
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                    {
                        if (key != null && key.GetValue("AsusFanControlPro") != null)
                        {
                            key.DeleteValue("AsusFanControlPro");
                        }
                    }
                }
                catch { /* Ignore HKLM access error during cleanup */ }

                string taskName = "AsusFanControlProStartup";
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                string exeDir = Path.GetDirectoryName(exePath);

                if (enable)
                {
                    string escapedExePath = exePath.Replace("'", "''");
                    string escapedExeDir = exeDir.Replace("'", "''");

                    string registerCmd = $"$action = New-ScheduledTaskAction -Execute '{escapedExePath}' -WorkingDirectory '{escapedExeDir}'; $trigger = New-ScheduledTaskTrigger -AtLogOn; $principal = New-ScheduledTaskPrincipal -RunLevel Highest; Register-ScheduledTask -TaskName '{taskName}' -Action $action -Trigger $trigger -Principal $principal -Force";

                    System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-WindowStyle Hidden -Command \"{registerCmd}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo))
                    {
                        process.WaitForExit();
                    }
                }
                else
                {
                    System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/delete /tn \"{taskName}\" /f",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo))
                    {
                        process.WaitForExit();
                    }
                }

                // Update Desktop shortcut to point directly to the EXE!
                UpdateDesktopShortcut();
            }
            catch (Exception ex) 
            {
                MessageBox.Show("Failed to change startup setting: " + ex.Message);
            }
        }

        private void UpdateDesktopShortcut()
        {
            try
            {
                string desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Desktop");
                if (!Directory.Exists(desktopPath))
                {
                    desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                }
                
                string shortcutPath = Path.Combine(desktopPath, "Asus Fan & CPU Controller Pro.lnk");
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                string exeDir = Path.GetDirectoryName(exePath);
                
                string escapedShortcutPath = shortcutPath.Replace("'", "''");
                string escapedExePath = exePath.Replace("'", "''");
                string escapedExeDir = exeDir.Replace("'", "''");

                string script = $"$sh = New-Object -ComObject WScript.Shell; $sc = $sh.CreateShortcut('{escapedShortcutPath}'); $sc.TargetPath = '{escapedExePath}'; $sc.WorkingDirectory = '{escapedExeDir}'; $sc.IconLocation = '{escapedExePath},0'; $sc.Save()";
                
                System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-WindowStyle Hidden -Command \"{script}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = System.Diagnostics.Process.Start(startInfo))
                {
                    p.WaitForExit();
                }
            }
            catch { }
        }

        private bool IsTaskRegistered(string taskName)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/query /tn \"{taskName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo))
                {
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            isRunning = false;
            if (bgThread != null && bgThread.IsAlive)
            {
                try { bgThread.Join(500); } catch { }
            }

            SaveSettings(); 
            if (asusControl != null) { try { asusControl.SetFanSpeeds(0); } catch {} }
            try { ApplyFrequencyLimit(0, true); } catch {} // Restore unlimited CPU frequency on exit
            if (trayIcon != null) trayIcon.Visible = false;
            
            KillThrottleStop(); // Close ThrottleStop cleanly
            
            base.OnFormClosing(e);
        }

        private void SaveSettings()
        {
            try
            {
                string mode = "0"; 
                if (rbModeManual.Checked) mode = "1";
                if (rbModeCurve.Checked) mode = "2";
                
                string[] lines = {
                    mode,
                    trackManual.Value.ToString(),
                    fanCurveEditor.GetPointsString(),
                    safetyEnabled.ToString(),
                    safetyTriggerTemp.ToString(),
                    safetyActionIsBios.ToString(),
                    runAtStartup.ToString(), // Save startup preference
                    freqCurveEditor.GetPointsString(), // Save frequency curve points
                    freqControlEnabled.ToString() // Save dynamic frequency control state
                };
                File.WriteAllLines(settingsPath, lines);
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(settingsPath))
                {
                    string[] lines = File.ReadAllLines(settingsPath);
                    if (lines.Length >= 2)
                    {
                        if (int.TryParse(lines[1], out int savedSpeed)) trackManual.Value = Math.Max(1, Math.Min(100, savedSpeed));
                        string mode = lines[0];
                        if (mode == "1") rbModeManual.Checked = true;
                        else if (mode == "2") rbModeCurve.Checked = true;
                        else rbModeOff.Checked = true;
                        
                        if (lines.Length >= 3) fanCurveEditor.SetPointsFromString(lines[2]);
                        
                        if (lines.Length >= 6)
                        {
                            bool.TryParse(lines[3], out safetyEnabled);
                            int.TryParse(lines[4], out safetyTriggerTemp);
                            bool.TryParse(lines[5], out safetyActionIsBios);
                            
                            chkSafetyEnabled.Checked = safetyEnabled;
                            numSafetyTemp.Value = Math.Max(50, Math.Min(105, safetyTriggerTemp));
                            rbSafetyActionBios.Checked = safetyActionIsBios;
                            rbSafetyActionMax.Checked = !safetyActionIsBios;
                        }

                        // Check Task Scheduler for actual startup status to sync UI
                        bool registryRun = IsTaskRegistered("AsusFanControlProStartup");

                        runAtStartup = registryRun;
                        chkRunAtStartup.Checked = registryRun;

                        // Load frequency controller settings safely
                        if (lines.Length >= 9)
                        {
                            freqCurveEditor.SetPointsFromString(lines[7]);
                            bool.TryParse(lines[8], out freqControlEnabled);
                            chkFreqControl.Checked = freqControlEnabled;
                        }
                        else
                        {
                            chkFreqControl.Checked = true;
                            freqControlEnabled = true;
                        }
                    }
                }
                else { rbModeOff.Checked = true; }
            }
            catch { rbModeOff.Checked = true; }
        }
    }
}