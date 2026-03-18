import tkinter as tk
from tkinter import messagebox, scrolledtext
import threading
import time
import mss
import numpy as np
import json
import os
import sqlite3
import pyodbc
import faulthandler
faulthandler.enable(open("Crash.log", "w"))

CONFIG_FILE = "config.json"
AUTO_SYNC_INTERVAL = 3600  # 1 hour

# ----------------Database -----------------
db_config = {
    "ip": "*****",
    "db_name": "*****",
    "schema": "*****",
    "username": "*****",
    "password": "*****"
}

def get_db(db_config=db_config, timeout=5):
    connection_string = (f'DRIVER={{SQL Server}};'
                         f'SERVER={db_config["ip"]};'
                         f'DATABASE={db_config["db_name"]};'
                         f'UID={db_config["username"]};'
                         f'PWD={db_config["password"]};'
                         f'Connection Timeout={timeout}')
    con = pyodbc.connect(connection_string)
    cur = con.cursor()
    return con, cur

def close_db(con, cur):
    cur.close()
    con.close()

# ---------------- Config Persistence -----------------
def save_config(config):
    with open(CONFIG_FILE, "w") as f:
        json.dump(config, f)

def load_config():
    if os.path.exists(CONFIG_FILE):
        with open(CONFIG_FILE, "r") as f:
            return json.load(f)
    return {"left": 10, "top": 400, "width": 210, "height": 100, "threshold": 15, "interval": 60, "auto_sync": 3600}

# ---------------- DB Logger -----------------
class DBLogger:
    def __init__(self, db_file="local_log.db"):
        self.db_file = db_file
        self._init_db()

    def _init_db(self):
        conn = sqlite3.connect(self.db_file)
        cursor = conn.cursor()
        cursor.execute("""
            CREATE TABLE IF NOT EXISTS local (
                ID INTEGER PRIMARY KEY AUTOINCREMENT,
                TANK TEXT,
                POWER_STATUS INTEGER,
                CREATED_DATE TEXT,
                SYNCED INTEGER DEFAULT 0
            )
        """)
        conn.commit()
        conn.close()

    def log(self, timestamp, machine_on, tank='A'):
        conn = sqlite3.connect(self.db_file)
        cursor = conn.cursor()
        cursor.execute("INSERT INTO local (TANK, POWER_STATUS, CREATED_DATE, SYNCED) VALUES (?, ?, ?, 0)",
                       (tank, int(machine_on), timestamp))
        conn.commit()
        conn.close()

# ---------------- Monitoring -----------------
class ScreenMonitor(threading.Thread):
    def __init__(self, region, threshold, interval, auto_sync,log_callback, db_logger=None, cloud_log_callback=None):
        super().__init__(daemon=True)
        self.region = region
        self.threshold = threshold
        self.interval = interval
        self.auto_sync = auto_sync
        self.log_callback = log_callback
        self.db_logger = db_logger
        self.cloud_log_callback = cloud_log_callback
        self._stop_event = threading.Event()

    def run(self):
        baseline_img = None
        with mss.mss() as sct:
            while not self._stop_event.is_set():
                img = np.array(sct.grab(self.region))[:, :, :3]
                if baseline_img is None:
                    baseline_img = img
                    machine_on = False
                else:
                    diff = np.abs(img.astype(int) - baseline_img.astype(int))
                    machine_on = np.mean(diff) > self.threshold

                timestamp = time.strftime("%Y-%m-%d %H:%M:%S")
                log_line = f"{timestamp} - STATUS: {machine_on}"

                # Log to GUI
                self.log_callback(log_line)

                # Local file log
                with open("log.txt", "a") as f:
                    f.write(log_line + "\n")

                # SQLite log
                if self.db_logger:
                    self.db_logger.log(timestamp, machine_on)

                # Cloud log (non-blocking)
                if self.cloud_log_callback:
                    self.cloud_log_callback(timestamp, machine_on)

                self._stop_event.wait(self.interval)

    def start_monitoring(self):
        self._stop_event.clear()
        self.start()

    def stop_monitoring(self):
        self._stop_event.set()

# ---------------- Overlay -----------------
class OverlayWindow(tk.Toplevel):
    def __init__(self, region):
        super().__init__()
        self.region = region
        self.overrideredirect(True)
        self.attributes("-topmost", True)
        self.attributes("-transparentcolor", "white")
        self.attributes("-alpha", 0.7)
        self.config(bg="white")
        self.geometry(f"{region['width']}x{region['height']}+{region['left']}+{region['top']}")
        self.canvas = tk.Canvas(self, width=region['width'], height=region['height'],
                                bg="white", highlightthickness=0)
        self.canvas.pack()
        self.rect = self.canvas.create_rectangle(0, 0, region['width'], region['height'],
                                                 outline="lime", width=3)

# ---------------- UI -----------------
class MonitorApp(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("Screen Monitor")
        self.geometry("500x450")
        self.resizable(False, False)
        self.attributes("-topmost", True)

        # Load config
        config = load_config()
        self.left_var = tk.IntVar(value=config["left"])
        self.top_var = tk.IntVar(value=config["top"])
        self.width_var = tk.IntVar(value=config["width"])
        self.height_var = tk.IntVar(value=config["height"])
        self.threshold_var = tk.DoubleVar(value=config["threshold"])
        self.interval_var = tk.IntVar(value=config["interval"])
        self.auto_sync_var = tk.IntVar(value=config["auto_sync"])

        # UI Elements
        tk.Label(self, text="Left:").grid(row=0, column=0)
        tk.Entry(self, textvariable=self.left_var).grid(row=0, column=1)
        tk.Label(self, text="Top:").grid(row=1, column=0)
        tk.Entry(self, textvariable=self.top_var).grid(row=1, column=1)
        tk.Label(self, text="Width:").grid(row=2, column=0)
        tk.Entry(self, textvariable=self.width_var).grid(row=2, column=1)
        tk.Label(self, text="Height:").grid(row=3, column=0)
        tk.Entry(self, textvariable=self.height_var).grid(row=3, column=1)
        tk.Label(self, text="Threshold:").grid(row=4, column=0)
        tk.Entry(self, textvariable=self.threshold_var).grid(row=4, column=1)
        tk.Label(self, text="Interval (sec):").grid(row=5, column=0)
        tk.Entry(self, textvariable=self.interval_var).grid(row=5, column=1)
        tk.Label(self, text="Auto Sync Interval (sec):").grid(row=6, column=0)
        tk.Entry(self, textvariable=self.auto_sync_var).grid(row=6, column=1)

        # Cloud status indicator
        tk.Label(self, text="Cloud:").grid(row=0, column=2)
        self.cloud_indicator = tk.Canvas(self, width=20, height=20)
        self.cloud_indicator.grid(row=0, column=3)
        self.cloud_status = False
        self.update_cloud_status(False)

        # Sync status
        tk.Label(self, text="Synced status").grid(row=3, column=2)
        self.sync_status = tk.Label(self, text="N/A")
        self.sync_status.grid(row=4, column=2)

        self.sync_btn = tk.Button(self, text="Sync Now", command=self.sync_data)
        self.sync_btn.grid(row=5, column=2)

        self.start_btn = tk.Button(self, text="Start Monitoring", command=self.toggle_monitoring)
        self.start_btn.grid(row=7, column=0, columnspan=2, pady=10)

        self.log_text = scrolledtext.ScrolledText(self, width=65, height=15, state=tk.DISABLED)
        self.log_text.grid(row=8, column=0, columnspan=4, padx=5, pady=5)

        # Initialize variables
        self.monitor_thread = None
        self.overlay = None
        self.db_logger = DBLogger()
        self._auto_sync_running = False

    def update_cloud_status(self, connected: bool):
        self.cloud_status = connected
        color = "green" if connected else "red"
        self.cloud_indicator.delete("all")
        self.cloud_indicator.create_rectangle(0, 0, 20, 20, fill=color)

    def cloud_log_async(self, timestamp, machine_on):
        def _log():
            try:
                con, cur = get_db(timeout=3)
                cur.execute(f"INSERT INTO [{db_config['schema']}].[CHEMICAL_TANK_STATUS] "
                            f"(TANK, POWER_STATUS, CREATED_DATE) VALUES (?, ?, ?)",
                            ('A', int(machine_on), timestamp))
                con.commit()
                close_db(con, cur)

                # Mark as synced in local DB
                conn = sqlite3.connect(self.db_logger.db_file)
                cursor = conn.cursor()
                cursor.execute("UPDATE local SET SYNCED = 1 WHERE CREATED_DATE = ?", (timestamp,))
                conn.commit()
                conn.close()


                self.update_cloud_status(True)
            except Exception:
                self.update_cloud_status(False)
        threading.Thread(target=_log, daemon=True).start()

    def toggle_monitoring(self):
        if self.monitor_thread and self.monitor_thread.is_alive():
            # Stop monitoring
            self.monitor_thread.stop_monitoring()
            self.monitor_thread.join(timeout=2)
            self.monitor_thread = None
            self.stop_auto_sync()

            if self.overlay:
                self.overlay.destroy()
                self.overlay = None
            self.start_btn.config(text="Start Monitoring")
            stop_msg = f"{time.strftime('%Y-%m-%d %H:%M:%S')} - Monitoring stopped."
            self.log(stop_msg)
            with open("log.txt", "a") as f:
                f.write(stop_msg + "\n")

            save_config({
                "left": self.left_var.get(),
                "top": self.top_var.get(),
                "width": self.width_var.get(),
                "height": self.height_var.get(),
                "threshold": self.threshold_var.get(),
                "interval": self.interval_var.get(),
                "auto_sync": self.auto_sync_var.get()
            })
        else:
            # Start monitoring
            region = {
                "left": self.left_var.get(),
                "top": self.top_var.get(),
                "width": self.width_var.get(),
                "height": self.height_var.get()
            }
            threshold = self.threshold_var.get()
            interval = self.interval_var.get()
            auto_sync = self.auto_sync_var.get()

            # Validation
            if region["width"] <= 0 or region["height"] <= 0:
                messagebox.showerror("Error", "Width and Height must be positive")
                return
            if interval <= 0:
                messagebox.showerror("Error", "Interval must be positive")
                return
            if auto_sync < 60 or auto_sync > 86400:
                messagebox.showerror("Error", "Auto Sync Interval must be between 60 and 86400 seconds(1 minute~24 hours)")
                return
            
            # Overlay
            self.overlay = OverlayWindow(region)
            self.overlay.update()

            # Start monitor thread
            self.monitor_thread = ScreenMonitor(
                region, threshold, interval, auto_sync, self.log,
                db_logger=self.db_logger,
                cloud_log_callback=self.cloud_log_async
            )
            self.monitor_thread.start_monitoring()
            self.start_auto_sync()
            self.start_btn.config(text="Stop Monitoring")
            start_msg = f"{time.strftime('%Y-%m-%d %H:%M:%S')} - Monitoring started."
            self.log(start_msg)
            with open("log.txt", "a") as f:
                f.write(start_msg + "\n")

    def log(self, message):
        self.log_text.config(state=tk.NORMAL)
        self.log_text.insert(tk.END, message + "\n")
        self.log_text.see(tk.END)
        self.log_text.config(state=tk.DISABLED)

    def sync_data(self):
        def _sync():
            try:
                con, cur = get_db(timeout=3)
                self.update_cloud_status(True)
            except Exception as e:
                self.update_cloud_status(False)
                self.sync_status.config(text="Not connected!")
                self.log(f"Cloud sync failed: {e}")
                return

            local_con = sqlite3.connect(self.db_logger.db_file)
            local_cur = local_con.cursor()
            local_cur.execute("SELECT ID, TANK, POWER_STATUS, CREATED_DATE FROM local WHERE SYNCED = 0")
            rows = local_cur.fetchall()

            if not rows:
                self.sync_status.config(text="Sync: No new data")
                local_con.close()
                close_db(con, cur)
                return

            synced_count = 0
            for row in rows:
                local_id, tank, power_status, created_date = row
                try:
                    cur.execute(
                        "INSERT INTO [electric].[CHEMICAL_TANK_STATUS] (TANK, POWER_STATUS, CREATED_DATE) VALUES (?, ?, ?)",
                        (tank, power_status, created_date)
                    )
                    local_cur.execute("UPDATE local SET SYNCED = 1 WHERE ID = ?", (local_id,))
                    synced_count += 1
                except Exception as e:
                    self.log(f"Failed to sync ID {local_id}: {e}")

            local_con.commit()
            local_con.close()
            con.commit()
            close_db(con, cur)
            self.sync_status.config(text=f"Last sync: {time.strftime('%Y-%m-%d %H:%M:%S')}")
            self.log(f"Synced {synced_count} rows to cloud.")

        self.sync_status.config(text="Syncing...")
        threading.Thread(target=_sync, daemon=True).start()

    # ---------------- Auto Sync -------------------
    def start_auto_sync(self):
        if self._auto_sync_running:
            return
        self._auto_sync_running = True

        def auto_sync_loop():
            while self._auto_sync_running:
                self.sync_data()
                time.sleep(self.auto_sync_var.get())

        threading.Thread(target=auto_sync_loop, daemon=True).start()

    def stop_auto_sync(self):
        self._auto_sync_running = False

# ---------------- Run -----------------
if __name__ == "__main__":
    app = MonitorApp()
    app.mainloop()
