# AI-HANDOFF: Asus Dynamic Frequency Controller (V2)
# VERSION UPGRADE LOG:
#   - SWAPPED AXES: X-axis is now CPU Load (%) flowing left-to-right. Y-axis is CPU Frequency (GHz) going up-and-down.
#   - VERTICAL DRAGGING: The 5 control points are now fixed on the X-axis (0%, 25%, 50%, 75%, 100% Load) 
#     and are dragged vertically to adjust the target frequency limit. This is highly intuitive visually.
#   - EFFECTIVE CLOCK SYNC: To ensure the "Effective Clock" shown in Core Temp/Task Manager matches the target limit perfectly,
#     we force-disable CPU idle/C-states (`IDLEDISABLE = 1`) while the controller is running. 
#   - THERMAL CONTROL AT IDLE: Even with C-states disabled, the CPU stays extremely cool (40s/50s) at idle 
#     because we dynamically lock the physical clock speed low (e.g., 1.2 GHz) which forces the CPU voltage to drop to minimum (~0.7V).
#   - PREVIOUS LOG: Standard Windows power plan enabled C-states, causing a large gap between the maximum frequency limit 
#     and the reported Effective Clock. Disabling idle states locks them in perfect 1:1 synchronization.

import os
import sys
import json
import time
import threading
import subprocess
import psutil
import win32com.client
import tkinter as tk
import customtkinter as ctk
from matplotlib.figure import Figure
from matplotlib.backends.backend_tkagg import FigureCanvasTkAgg

# Config paths
CONFIG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "dynamic_frequency_config.json")
DEFAULT_PLAN_GUID = "3ff9831b-6f80-4830-8178-736cd4229e7b"  # Active power plan
FREQ_LIMIT_GUID = "75b0ae3f-bce0-45a7-8c89-c9611c25e100"  # Maximum processor frequency setting
IDLE_DISABLE_GUID = "5d76a2ca-e8c0-402f-a133-2158492d58ad" # Processor idle disable setting

class DraggablePlot:
    """Manages the interactive Matplotlib curve editor with vertical dragging (X=Load, Y=Frequency)."""
    def __init__(self, ax, canvas, points_x, points_y, on_change_callback):
        self.ax = ax
        self.canvas = canvas
        self.points_x = list(points_x)  # CPU Load levels (0%, 25%, 50%, 75%, 100%) - Fixed
        self.points_y = list(points_y)  # Target Frequencies in GHz - Draggable Vertically
        self.on_change = on_change_callback
        self.active_idx = None
        
        # Draw curve line and point markers
        self.line, = ax.plot(self.points_x, self.points_y, color="#00d2ff", linewidth=3, label="Target Curve")
        self.dots, = ax.plot(self.points_x, self.points_y, "o", color="#ff007f", markersize=10, picker=10)
        
        # Connect mouse events
        self.canvas.mpl_connect("button_press_event", self.on_press)
        self.canvas.mpl_connect("button_release_event", self.on_release)
        self.canvas.mpl_connect("motion_notify_event", self.on_motion)
        
    def on_press(self, event):
        if event.inaxes != self.ax:
            return
        # Find closest point on the X-axis (since X load markers are fixed at 0, 25, 50, 75, 100)
        min_dist = float("inf")
        closest_idx = None
        for i, (px, py) in enumerate(zip(self.points_x, self.points_y)):
            dx = (px - event.xdata) / 100.0
            dy = (py - event.ydata) / 3.3
            dist = (dx**2 + dy**2)**0.5
            if dist < min_dist and dist < 0.15:  # Tolerance radius
                min_dist = dist
                closest_idx = i
                
        if closest_idx is not None:
            self.active_idx = closest_idx
            
    def on_release(self, event):
        self.active_idx = None
        self.on_change()
        
    def on_motion(self, event):
        if self.active_idx is None or event.inaxes != self.ax:
            return
        
        # Constrain frequency (Y-axis) between 0.8 GHz and 4.1 GHz (hardware limits)
        new_y = max(0.8, min(4.1, event.ydata))
        
        self.points_y[self.active_idx] = new_y
        
        # Update lines and redraw
        self.line.set_data(self.points_x, self.points_y)
        self.dots.set_data(self.points_x, self.points_y)
        self.canvas.draw_idle()

class DynamicFrequencyApp(ctk.CTk):
    def __init__(self):
        super().__init__()
        
        self.title("Asus Dynamic Frequency Controller")
        self.geometry("900x560")
        ctk.set_appearance_mode("dark")
        ctk.set_default_color_theme("blue")
        
        self.configure(fg_color="#121212")
        
        # State variables
        self.is_running = True
        self.last_applied_freq = None
        self.current_load = 0.0
        self.current_temp = 0.0
        
        self.load_config()
        self.create_widgets()
        
        # Start background loop
        self.monitor_thread = threading.Thread(target=self.background_loop, daemon=True)
        self.monitor_thread.start()
        
    def load_config(self):
        # Default presets: X=Load, Y=Freq
        default_x = [0, 25, 50, 75, 100]
        default_y = [1.2, 2.0, 2.8, 3.6, 4.1]
        
        if os.path.exists(CONFIG_PATH):
            try:
                with open(CONFIG_PATH, "r") as f:
                    data = json.load(f)
                    # Support legacy migration: if loaded config had frequency on X, swap them
                    loaded_x = data["curve_x"]
                    loaded_y = data["curve_y"]
                    
                    if len(loaded_x) == 5 and loaded_x[0] > 0.5 and loaded_x[-1] <= 4.2:
                        # Legacy format (X=Freq, Y=Load) -> swap to new format
                        self.curve_x = loaded_y
                        self.curve_y = loaded_x
                    else:
                        self.curve_x = loaded_x
                        self.curve_y = loaded_y
                        
                    self.controller_enabled = data.get("enabled", True)
                    return
            except:
                pass
        
        self.curve_x = default_x
        self.curve_y = default_y
        self.controller_enabled = True
        
    def save_config(self):
        try:
            with open(CONFIG_PATH, "w") as f:
                json.dump({
                    "curve_x": self.plot_manager.points_x,
                    "curve_y": self.plot_manager.points_y,
                    "enabled": self.controller_enabled
                }, f)
        except Exception as e:
            print(f"Error saving config: {e}")
            
    def create_widgets(self):
        # -------------------------------------------------------------
        # LEFT CONTROL PANEL (DASHBOARD)
        # -------------------------------------------------------------
        self.left_panel = ctk.CTkFrame(self, width=280, fg_color="#1a1a1a", corner_radius=15)
        self.left_panel.pack(side="left", fill="y", padx=15, pady=15)
        self.left_panel.pack_propagate(False)
        
        # Title Label
        self.title_lbl = ctk.CTkLabel(self.left_panel, text="ASUS DYNAMIC CPU\nCONTROLLER", font=("Outfit", 18, "bold"), text_color="#00d2ff")
        self.title_lbl.pack(pady=20)
        
        # Enabled Toggle Switch
        self.toggle_switch = ctk.CTkSwitch(
            self.left_panel, 
            text="Active Service", 
            font=("Inter", 14, "bold"),
            command=self.toggle_service,
            progress_color="#00d2ff"
        )
        if self.controller_enabled:
            self.toggle_switch.select()
        self.toggle_switch.pack(pady=15)
        
        self.divider = ctk.CTkFrame(self.left_panel, height=2, fg_color="#333333")
        self.divider.pack(fill="x", padx=20, pady=10)
        
        # Load
        self.load_lbl = ctk.CTkLabel(self.left_panel, text="CPU Load", font=("Inter", 12, "bold"), text_color="#aaaaaa")
        self.load_lbl.pack(pady=(10, 2))
        self.load_val = ctk.CTkLabel(self.left_panel, text="0.0%", font=("Outfit", 32, "bold"), text_color="#ffffff")
        self.load_val.pack()
        
        # Temp
        self.temp_lbl = ctk.CTkLabel(self.left_panel, text="CPU Temperature", font=("Inter", 12, "bold"), text_color="#aaaaaa")
        self.temp_lbl.pack(pady=(15, 2))
        self.temp_val = ctk.CTkLabel(self.left_panel, text="0.0°C", font=("Outfit", 32, "bold"), text_color="#ff007f")
        self.temp_val.pack()
        
        # Active Limit
        self.freq_lbl = ctk.CTkLabel(self.left_panel, text="Effective Lock Limit", font=("Inter", 12, "bold"), text_color="#aaaaaa")
        self.freq_lbl.pack(pady=(15, 2))
        self.freq_val = ctk.CTkLabel(self.left_panel, text="N/A", font=("Outfit", 28, "bold"), text_color="#a8ff00")
        self.freq_val.pack()
        
        # Reset Defaults Button
        self.reset_btn = ctk.CTkButton(
            self.left_panel, 
            text="Reset Defaults", 
            fg_color="#333333", 
            hover_color="#444444",
            text_color="#ffffff",
            corner_radius=8,
            command=self.reset_defaults
        )
        self.reset_btn.pack(side="bottom", pady=20)
        
        # -------------------------------------------------------------
        # RIGHT PANEL (CURVE EDITOR CANVAS)
        # -------------------------------------------------------------
        self.right_panel = ctk.CTkFrame(self, fg_color="#1a1a1a", corner_radius=15)
        self.right_panel.pack(side="right", expand=True, fill="both", padx=(0, 15), pady=15)
        
        self.chart_lbl = ctk.CTkLabel(self.right_panel, text="Interactive Dynamic Scaling Curve (Drag points vertically)", font=("Inter", 14, "bold"), text_color="#ffffff")
        self.chart_lbl.pack(pady=(15, 5))
        
        # Initialize Matplotlib Figure & Embedded Canvas
        self.fig = Figure(figsize=(6, 4.5), dpi=100, facecolor="#1a1a1a")
        self.ax = self.fig.add_subplot(111)
        self.ax.set_facecolor("#1a1a1a")
        
        # Style Chart Grid & Borders
        self.ax.spines['bottom'].set_color('#444444')
        self.ax.spines['top'].set_color('#444444')
        self.ax.spines['left'].set_color('#444444')
        self.ax.spines['right'].set_color('#444444')
        self.ax.tick_params(colors='#ffffff', labelsize=10)
        self.ax.grid(True, color="#333333", linestyle="--")
        
        # Set swapped axes labels (X = Load, Y = Frequency)
        self.ax.set_xlabel("CPU Load (%)", color="#ffffff", labelpad=10, fontname="Inter")
        self.ax.set_ylabel("Target Frequency (GHz)", color="#ffffff", labelpad=10, fontname="Inter")
        self.ax.set_xlim(-5, 105)
        self.ax.set_ylim(0.6, 4.3)
        
        # Pack canvas to UI
        self.canvas_widget = FigureCanvasTkAgg(self.fig, master=self.right_panel)
        self.canvas_widget.get_tk_widget().pack(fill="both", expand=True, padx=15, pady=15)
        
        # Draggable plot manager
        self.plot_manager = DraggablePlot(self.ax, self.canvas_widget, self.curve_x, self.curve_y, self.save_config)
        
    def toggle_service(self):
        self.controller_enabled = self.toggle_switch.get() == 1
        self.save_config()
        if not self.controller_enabled:
            # Revert CPU to completely unlimited & re-enable idle states
            self.apply_hardware_limit(0, enable_idle=True)
            self.freq_val.configure(text="Unlimited (Full)")
            
    def reset_defaults(self):
        self.plot_manager.points_y = [1.2, 2.0, 2.8, 3.6, 4.1]
        self.plot_manager.line.set_data(self.plot_manager.points_x, self.plot_manager.points_y)
        self.plot_manager.dots.set_data(self.plot_manager.points_x, self.plot_manager.points_y)
        self.canvas_widget.draw_idle()
        self.save_config()
        
    def get_wmi_temperature(self):
        try:
            wmi_service = win32com.client.GetObject("winmgmts:\\\\.\\root\\wmi")
            temperatures = wmi_service.ExecQuery("SELECT * FROM MSAcpi_ThermalZoneTemperature")
            for t in temperatures:
                return (t.CurrentTemperature - 2732) / 10.0
        except:
            pass
        return 0.0
        
    def background_loop(self):
        while self.is_running:
            try:
                # 1. Update Load Status UI
                load = psutil.cpu_percent()
                self.current_load = load
                self.load_val.configure(text=f"{load:.1f}%")
                
                # 2. Update Temp
                temp = self.get_wmi_temperature()
                if temp > 0:
                    self.current_temp = temp
                    self.temp_val.configure(text=f"{temp:.1f}°C")
                    if temp > 75:
                        self.temp_val.configure(text_color="#ff0000")
                    elif temp > 65:
                        self.temp_val.configure(text_color="#ff9900")
                    else:
                        self.temp_val.configure(text_color="#ff007f")
                
                # 3. Dynamic Scaling Logic
                if self.controller_enabled:
                    target_freq = self.interpolate_frequency(load)
                    target_mhz = int(round(target_freq * 10) * 100)
                    
                    # Enforce settings with a 100 MHz deadband
                    if self.last_applied_freq is None or abs(target_mhz - self.last_applied_freq) >= 100:
                        # Disable CPU idle states to keep the Effective Clock perfectly in sync
                        self.apply_hardware_limit(target_mhz, enable_idle=False)
                        self.last_applied_freq = target_mhz
                        self.freq_val.configure(text=f"{target_mhz / 1000.0:.2f} GHz")
                else:
                    self.freq_val.configure(text="Unlimited (Full)")
                    
            except Exception as e:
                pass
            time.sleep(1.0)
            
    def interpolate_frequency(self, load):
        px = self.plot_manager.points_x  # Load [0, 25, 50, 75, 100]
        py = self.plot_manager.points_y  # Freq (Draggable)
        
        if load <= px[0]:
            return py[0]
        if load >= px[-1]:
            return py[-1]
            
        for i in range(len(px) - 1):
            if px[i] <= load <= px[i+1]:
                # Linear interpolation
                return py[i] + (load - px[i]) * (py[i+1] - py[i]) / (px[i+1] - px[i])
        return py[-1]
        
    def apply_hardware_limit(self, mhz, enable_idle=False):
        """Updates frequency limits and configures CPU idle states to sync the Effective Clock."""
        try:
            info = subprocess.STARTUPINFO()
            info.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            info.wShowWindow = subprocess.SW_HIDE
            
            # 1. Update frequency limits
            subprocess.run([
                "powercfg", "/setacvalueindex", DEFAULT_PLAN_GUID, "SUB_PROCESSOR", FREQ_LIMIT_GUID, str(mhz)
            ], startupinfo=info, capture_output=True)
            subprocess.run([
                "powercfg", "/setdcvalueindex", DEFAULT_PLAN_GUID, "SUB_PROCESSOR", FREQ_LIMIT_GUID, str(mhz)
            ], startupinfo=info, capture_output=True)
            
            # 2. Update IDLEDISABLE state (1 = disable idle to sync Effective Clock, 0 = enable idle to save power)
            idle_val = 0 if enable_idle else 1
            subprocess.run([
                "powercfg", "/setacvalueindex", DEFAULT_PLAN_GUID, "SUB_PROCESSOR", IDLE_DISABLE_GUID, str(idle_val)
            ], startupinfo=info, capture_output=True)
            subprocess.run([
                "powercfg", "/setdcvalueindex", DEFAULT_PLAN_GUID, "SUB_PROCESSOR", IDLE_DISABLE_GUID, str(idle_val)
            ], startupinfo=info, capture_output=True)
            
            # 3. Apply changes immediately
            subprocess.run([
                "powercfg", "/setactive", DEFAULT_PLAN_GUID
            ], startupinfo=info, capture_output=True)
        except Exception as e:
            pass
            
    def on_closing(self):
        self.is_running = False
        # Clean up and restore CPU to full speed and enable C-states on exit
        self.apply_hardware_limit(0, enable_idle=True)
        self.destroy()

if __name__ == "__main__":
    app = DynamicFrequencyApp()
    app.protocol("WM_DELETE_WINDOW", app.on_closing)
    app.mainloop()
