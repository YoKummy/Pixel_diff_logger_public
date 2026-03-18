// Single-file WinForms app. Target: .NET Framework 4.7.2 (x86)
// Requires NuGet package: System.Data.SQLite.Core

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Data.SQLite;
using System.Data.SqlClient;
using System.Text.Json;
using System.Collections.Generic;
using System.Globalization;
using System.Security.AccessControl;


namespace win7v3
{
    static class Program
    {

        [STAThread]
        static void Main()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { File.AppendAllText("Crash.log", DateTime.Now + " Unhandled: " + e.ExceptionObject + "\n"); } catch { }
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MonitorForm());
        }
    }

    public class EDFParser
    {
        public class EDFData
        {
            public string DATA_TYPE { get; set; }  // "amps" or "volts"
            public double DATA_VALUE { get; set; }
            public DateTime DATA_TIME { get; set; }
        }

        public static List<EDFData> ParseEDFFile(string filePath)
        {
            var result = new List<EDFData>();
            var dataDict = new Dictionary<int, Dictionary<string, object>>();

            string filename = Path.GetFileName(filePath);
            string dtStr = Path.GetFileNameWithoutExtension(filename).Split('-')[^1]; // last part after dash
            if (!DateTime.TryParseExact(dtStr, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime startTime))
            {
                startTime = DateTime.Now; // fallback
            }

            foreach (var line in File.ReadLines(filePath))
            {
                var row = line.Split(',');
                if (row.Length < 3) continue;

                string a = row[0].Trim();
                string b = row[1].Trim();
                string c = row[2].Trim();

                if (!int.TryParse(b, out int seriesId)) continue;

                if (!dataDict.ContainsKey(seriesId))
                    dataDict[seriesId] = new Dictionary<string, object>();

                if (a == "1" || a == "2")
                {
                    if (!double.TryParse(c, out double val))
                        val = 0.0;
                    dataDict[seriesId][a] = val;
                }
                else if (a == "0")
                {
                    if (!double.TryParse(c, out double minutesOffset))
                        minutesOffset = 0.0;
                    dataDict[seriesId]["abs_time"] = startTime.AddMinutes(minutesOffset);
                }
            }

            foreach (var series in dataDict.Values)
            {
                foreach (var dtype in new[] { "1", "2" })
                {
                    if (series.ContainsKey(dtype) && series.ContainsKey("abs_time"))
                    {
                        result.Add(new EDFData
                        {
                            DATA_TYPE = dtype == "1" ? "amps" : "volts",
                            DATA_VALUE = (double)series[dtype],
                            DATA_TIME = (DateTime)series["abs_time"]
                        });
                    }
                }
            }

            return result;
        }
    }
    
    public class EDFUploader
    {
        private readonly string _cloudIp;
        private readonly string _cloudDb;
        private readonly string _cloudSchema;
        private readonly string _cloudUser;
        private readonly string _cloudPass;

        public EDFUploader(string cloudIp, string cloudDb, string cloudSchema, string cloudUser, string cloudPass)
        {
            _cloudIp = cloudIp;
            _cloudDb = cloudDb;
            _cloudSchema = cloudSchema;
            _cloudUser = cloudUser;
            _cloudPass = cloudPass;
        }

        public bool InsertEDFToDb(string filePath, string oldEdfFolder, string tank)
        {
            try
            {
                var dataList = EDFParser.ParseEDFFile(filePath);

                string connString = $"Data Source={_cloudIp};Initial Catalog={_cloudDb};User ID={_cloudUser};Password={_cloudPass};Connect Timeout=3";
                using (var con = new SqlConnection(connString))
                {
                    con.Open();
                    foreach (var d in dataList)
                    {
                        using (var cmd = new SqlCommand(
                            $"INSERT INTO [{_cloudSchema}].[CHEMICAL_TANK_STATUS] (TANK, DATA_TYPE, DATA_VALUE, DATA_TIME) VALUES (@t, @dt, @p, @d)", 
                            con))
                        {
                            cmd.Parameters.AddWithValue("@t", tank);
                            cmd.Parameters.AddWithValue("@dt", d.DATA_TYPE);
                            cmd.Parameters.AddWithValue("@p", d.DATA_VALUE);
                            cmd.Parameters.AddWithValue("@d", d.DATA_TIME);
                            cmd.ExecuteNonQuery();
                        }
                    }
                }

                // move file to old EDF folder
                Directory.CreateDirectory(oldEdfFolder); // ensure folder exists
                string destFile = Path.Combine(oldEdfFolder, Path.GetFileName(filePath));
                if (File.Exists(destFile)) File.Delete(destFile); // overwrite if exists
                File.Move(filePath, destFile);

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to insert EDF: " + ex.Message);
                return false;
            }
        }
    }

    public class DBLogger
    {
        private readonly string _dbFile;
        public DBLogger(string dbFile = "local_log.db")
        {
            _dbFile = dbFile;
            InitDb();
        }

        private void InitDb()
        {
            if (!File.Exists(_dbFile))
            {
                SQLiteConnection.CreateFile(_dbFile);
            }

            using (var c = new SQLiteConnection($"Data Source={_dbFile};Version=3;"))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = @"CREATE TABLE IF NOT EXISTS local (
                        ID INTEGER PRIMARY KEY AUTOINCREMENT,
                        TANK TEXT,
                        CREATED_DATE TEXT,
                        DATA_TYPE TEXT,
                        DATA_VALUE FLOAT,
                        SYNCED INTEGER DEFAULT 0
                    )";
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void Log(string timestamp, bool machineOn, string tank)
        {

            using (var c = new SQLiteConnection($"Data Source={_dbFile};Version=3;"))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO local (TANK, CREATED_DATE, DATA_TYPE, DATA_VALUE, SYNCED) VALUES (@t, @d, @dt, @p, 0)";
                    cmd.Parameters.AddWithValue("@t", tank);
                    cmd.Parameters.AddWithValue("@d", timestamp);
                    cmd.Parameters.AddWithValue("@dt", "power");
                    cmd.Parameters.AddWithValue("@p", machineOn ? 1.0 : 0.0);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public string DbFile => _dbFile;
    }

    public class ScreenMonitor
    {
        private Rectangle _region;
        private readonly string _tank;
        private double _threshold;
        private int _intervalSec;
        private volatile bool _running;
        private Task _task;
        private readonly Action<string> _logCallback;
        private readonly DBLogger _dbLogger;
        private readonly Func<string, bool, string, Task> _cloudLogAsync;
        private readonly Action _autoProcessEdf;

        public ScreenMonitor(Rectangle region, double threshold, int intervalSec, DBLogger dbLogger, Action<string> logCallback, Func<string, bool, string, Task> cloudLogAsync, string tank, Action autoProcessEdf)
        {
            _region = region;
            _threshold = threshold;
            _intervalSec = intervalSec;
            _dbLogger = dbLogger;
            _logCallback = logCallback;
            _cloudLogAsync = cloudLogAsync;
            _tank = tank;
            _autoProcessEdf = autoProcessEdf;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _task = Task.Run(MonitorLoop);
        }

        public void Stop()
        {
            _running = false;
            _task?.Wait(2000);
        }

        private Bitmap Capture()
        {
            var bmp = new Bitmap(_region.Width, _region.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(_region.Left, _region.Top, 0, 0, _region.Size, CopyPixelOperation.SourceCopy);
            }
            return bmp;
        }

        private double MeanAbsDiff(Bitmap a, Bitmap b)
        {
            if (a.Width != b.Width || a.Height != b.Height) return double.MaxValue;

            var rect = new Rectangle(0, 0, a.Width, a.Height);
            var bdA = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var bdB = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

            try
            {
                int strideA = Math.Abs(bdA.Stride);
                int strideB = Math.Abs(bdB.Stride);
                unsafe
                {
                    byte* ptrA = (byte*)bdA.Scan0;
                    byte* ptrB = (byte*)bdB.Scan0;
                    long sum = 0;
                    int pixels = a.Width * a.Height;
                    for (int y = 0; y < a.Height; y++)
                    {
                        byte* rowA = ptrA + y * bdA.Stride;
                        byte* rowB = ptrB + y * bdB.Stride;
                        for (int x = 0; x < a.Width; x++)
                        {
                            int idx = x * 3;
                            int da = Math.Abs(rowA[idx] - rowB[idx]);
                            int db = Math.Abs(rowA[idx + 1] - rowB[idx + 1]);
                            int dc = Math.Abs(rowA[idx + 2] - rowB[idx + 2]);
                            sum += da + db + dc;
                        }
                    }
                    // average per channel per pixel
                    double mean = (double)sum / (pixels * 3);
                    return mean;
                }
            }
            finally
            {
                a.UnlockBits(bdA);
                b.UnlockBits(bdB);
            }
        }

        private async Task MonitorLoop()
        {
            Bitmap baseline = null;
            bool? lastMachineOn = null;
            DateTime? machineOffTime = null;
            TimeSpan edfDelay = TimeSpan.FromMinutes(1);
            while (_running)
            {
                try
                {
                    using (var bmp = Capture())
                    {
                        bool machineOn = false;
                        if (baseline == null)
                        {
                            baseline = (Bitmap)bmp.Clone();
                            machineOn = false;
                        }
                        else
                        {
                            double diff = MeanAbsDiff(bmp, baseline) * 10;
                            machineOn = diff > _threshold;
                        }

                        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        string logLine = $"{timestamp} - STATUS: {machineOn}";

                        _logCallback?.Invoke(logLine);
                        File.AppendAllText("log.txt", logLine + Environment.NewLine);
                        _dbLogger?.Log(timestamp, machineOn, _tank);

                        if (_cloudLogAsync != null)
                        {
                            await _cloudLogAsync(timestamp, machineOn, _tank);
                        }

                        if (lastMachineOn.HasValue)
                        {
                            if (lastMachineOn.Value && !machineOn)
                            {
                                // Transition: ON -> OFF
                                machineOffTime = DateTime.Now;
                            }
                            else if (!machineOn && machineOffTime.HasValue)
                            {
                                // Machine is still OFF, check delay
                                if (DateTime.Now - machineOffTime.Value >= edfDelay)
                                {
                                    _autoProcessEdf?.Invoke();  // call your EDF method
                                    machineOffTime = null;  // reset timer
                                }
                            }
                            else if (machineOn)
                            {
                                // Machine turned ON again, cancel timer
                                machineOffTime = null;
                            }
                        }
                        lastMachineOn = machineOn;
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText("Crash.log", DateTime.Now + " Monitor error: " + ex + Environment.NewLine);
                }

                int waited = 0;
                while (_running && waited < _intervalSec * 1000)
                {
                    await Task.Delay(200);
                    waited += 200;
                }
            }
        }
    }

    public class OverlayForm : Form
    {
        private Rectangle _region;
        public OverlayForm(Rectangle region)
        {
            _region = region;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.White;
            TransparencyKey = Color.White; // make white transparent
            Opacity = 0.7;
            Width = region.Width;
            Height = region.Height;
            Left = region.Left;
            Top = region.Top;
            this.Paint += OverlayForm_Paint;
        }

        private void OverlayForm_Paint(object sender, PaintEventArgs e)
        {
            using (var pen = new Pen(Color.Lime, 3))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }
    }

    public class MonitorForm : Form
    {
        // UI controls
        NumericUpDown nudLeft, nudTop, nudWidth, nudHeight;
        NumericUpDown nudThreshold, nudInterval, nudAutoSync;
        Button btnStart, btnSync, btnImport, btnEdf, btnOldEdf;
        Panel pnlCloud;
        Label lblSyncStatus;
        TextBox txtLog, txtTank, txtEdf, txtOldEdf;

        ScreenMonitor _monitor;
        OverlayForm _overlay;
        DBLogger _dbLogger;
        volatile bool _autoSyncRunning;
        private const string ConfigFile = "config.json";
        private string _tank;
        private string _edfFolder;
        private string _oldEdfFolder;
        string edf = @"C:\EDF";
        string oldedf = @"C:\old_edf";

        private DateTime? machineOffTime = null;
        private readonly TimeSpan MachineOffDelay = TimeSpan.FromMinutes(1);

        // cloud DB settings
        readonly string cloudIp = "******";
        readonly string cloudDb = "******";
        readonly string cloudSchema = "******";
        readonly string cloudUser = "******";
        readonly string cloudPass = "******";

        public MonitorForm()
        {
            Text = "Screen Monitor";
            Width = 600;
            Height = 600;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            TopMost = true;

            InitializeComponents();

            _dbLogger = new DBLogger();

            LoadConfig();
            this.FormClosing += MonitorForm_FormClosing;
        }

        private void MonitorForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                // If monitoring is active, stop it gracefully
                try
                {
                    _monitor?.Stop();
                }
                catch { }
                _monitor = null;
                StopAutoSync();
                try { _overlay?.Close(); } catch { }
                _overlay = null;

                // Save config and log close reason
                SaveConfig();
                string reason = e.CloseReason == CloseReason.UserClosing ? "Closed by user" : ("Closed: " + e.CloseReason.ToString());
                string msg = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " - " + reason;
                Log(msg);
                try { File.AppendAllText("log.txt", msg + Environment.NewLine); } catch { }
            }
            catch { }
        }

        private void InitializeComponents()
        {
            int left = 10, top = 10, spacing = 30;

            CreateLabel("Tank:", left, top);
            txtTank = new TextBox
            {
                Left = left + 80,
                Top = top - 3, 
                Width = 80,
                Text = "A" 
            };
            Controls.Add(txtTank);
            top += spacing;
            CreateLabel("Left:", left, top);
            nudLeft = CreateNumeric(left + 80, top);
            top += spacing;
            CreateLabel("Top:", left, top);
            nudTop = CreateNumeric(left + 80, top);
            top += spacing;
            CreateLabel("Width:", left, top);
            nudWidth = CreateNumeric(left + 80, top, 210);
            top += spacing;
            CreateLabel("Height:", left, top);
            nudHeight = CreateNumeric(left + 80, top, 100);
            top += spacing;
            CreateLabel("Threshold:", left, top);
            nudThreshold = CreateNumeric(left + 80, top, 15, 0, 255);
            nudThreshold.DecimalPlaces = 1;
            nudThreshold.Increment = 0.1M;
            top += spacing;
            CreateLabel("Interval (sec):", left, top);
            nudInterval = CreateNumeric(left + 120, top, 60, 1, 3600);
            top += spacing;
            CreateLabel("Auto Sync (sec):", left, top);
            nudAutoSync = CreateNumeric(left + 120, top, 3600, 60, 86400);
            Label lblCloud = new Label
            {
                Text = "Cloud:",
                Location = new Point(300, 10),
                AutoSize = true
            };
            Controls.Add(lblCloud);

            pnlCloud = new Panel
            {
                Size = new Size(20, 20),
                Location = new Point(lblCloud.Right + 10, lblCloud.Top - 2),
                BackColor = Color.Red,
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(pnlCloud);

            CreateLabel("Synced status", 300, 60);
            lblSyncStatus = new Label { Left = 300, Top = 80, Width = 200, Text = "N/A" };
            Controls.Add(lblSyncStatus);

            btnSync = new Button { Left = 300, Top = 110, Width = 80, Text = "Sync Now" };
            btnSync.Click += BtnSync_Click;
            Controls.Add(btnSync);

            CreateLabel("EDF:", 300, 140);

            txtEdf = new TextBox { Left = 300, Top = 160, Width = 180, ReadOnly = true };
            Controls.Add(txtEdf);

            btnEdf = new Button { Left = 300, Top = 180, Width = 180, Text = "EDF folder" };
            btnEdf.Click += BtnEdf_Click;
            Controls.Add(btnEdf);

            CreateLabel("Old EDF:", 300, 200);

            txtOldEdf = new TextBox { Left = 300, Top = 220, Width = 180, ReadOnly = true };
            Controls.Add(txtOldEdf);
            
            btnOldEdf = new Button { Left = 300, Top = 240, Width = 180, Text = "Old EDF folder" };
            btnOldEdf.Click += BtnOldEdf_Click;
            Controls.Add(btnOldEdf);

            btnStart = new Button { Left = 10, Top = top + 50, Width = 180, Text = "Start Monitoring" };
            btnStart.Click += BtnStart_Click;
            Controls.Add(btnStart);

            btnImport = new Button { Left = 200, Top = top + 50, Width = 100, Text = "Import EDF" };
            btnImport.Click += BtnImport_Click;
            Controls.Add(btnImport);

            txtLog = new TextBox { Left = 10, Top = btnStart.Bottom + 10, Width = 480, Height = 190, Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true };
            Controls.Add(txtLog);

            txtTank.Text = "A";
            nudLeft.Value = 20; nudTop.Value = 420; nudWidth.Value = 180; nudHeight.Value = 60;
            nudThreshold.Value = 38; nudInterval.Value = 60; nudAutoSync.Value = 3600;
        }


        private void CreateLabel(string text, int x, int y)
        {
            var lbl = new Label { Left = x, Top = y, Width = 80, Text = text };
            Controls.Add(lbl);
        }

        private NumericUpDown CreateNumeric(int x, int y, decimal value = 0, decimal min = 0, decimal max = 100000)
        {
            var n = new NumericUpDown { Left = x, Top = y, Width = 80, Minimum = min, Maximum = max, Value = value };
            Controls.Add(n);
            return n;
        }

        private void AutoProcessEDF()
        {
            try
            {
                var files = Directory.GetFiles(_edfFolder, "*.edf");
                if (files.Length == 0) return;

                Array.Sort(files);
                Array.Reverse(files); // latest first
                string latestFile = files[0];

                var uploader = new EDFUploader(cloudIp, cloudDb, cloudSchema, cloudUser, cloudPass);
                bool success = uploader.InsertEDFToDb(latestFile, _oldEdfFolder, _tank);
                Log(success ? $"Auto EDF processed: {latestFile}" : $"Failed to process EDF: {latestFile}");
            }
            catch (Exception ex)
            {
                Log($"Auto EDF processing error: {ex.Message}");
            }
        }

        private class Config
        {
            public string Tank{ get; set; }
            public int Left { get; set; }
            public int Top { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public decimal Threshold { get; set; }
            public int Interval { get; set; }
            public int AutoSync { get; set; }
            public string EdfFolder { get; set; }
            public string OldEdfFolder{ get; set; }
        }

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigFile)) return;
                string json = File.ReadAllText(ConfigFile);
                var cfg = JsonSerializer.Deserialize<Config>(json);
                if (cfg == null) return;

                _tank = cfg.Tank ?? "A";
                txtTank.Text = _tank;
                nudLeft.Value = cfg.Left;
                nudTop.Value = cfg.Top;
                nudWidth.Value = cfg.Width;
                nudHeight.Value = cfg.Height;
                nudThreshold.Value = cfg.Threshold;
                nudInterval.Value = cfg.Interval;
                nudAutoSync.Value = cfg.AutoSync;
                _edfFolder = cfg.EdfFolder ?? @"C:\EDF";
                txtEdf.Text = _edfFolder;
                _oldEdfFolder = cfg.OldEdfFolder ?? @"C:\EDF_old";
                txtOldEdf.Text = _oldEdfFolder;
            }
            catch { }
        }
        

        private void SaveConfig()
        {
            try
            {
                var cfg = new Config
                {
                    Tank = txtTank.Text,
                    Left = (int)nudLeft.Value,
                    Top = (int)nudTop.Value,
                    Width = (int)nudWidth.Value,
                    Height = (int)nudHeight.Value,
                    Threshold = nudThreshold.Value,
                    Interval = (int)nudInterval.Value,
                    AutoSync = (int)nudAutoSync.Value,
                    EdfFolder = txtEdf.Text,
                    OldEdfFolder = txtOldEdf.Text
                };
                string json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigFile, json);
            }
            catch { }
        }

        private void BtnStart_Click(object sender, EventArgs e)
        {
            if (_monitor != null)
            {
                // stop
                _monitor.Stop();
                _monitor = null;
                StopAutoSync();
                _overlay?.Close(); _overlay = null;
                btnStart.Text = "Start Monitoring";
                var stopMsg = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " - Monitoring stopped.";
                Log(stopMsg);
                File.AppendAllText("log.txt", stopMsg + Environment.NewLine);
            }
            else
            {
                // validation
                if (nudWidth.Value <= 0 || nudHeight.Value <= 0)
                {
                    MessageBox.Show("Width and Height must be positive", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (nudInterval.Value <= 0)
                {
                    MessageBox.Show("Interval must be positive", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (nudAutoSync.Value < 60 || nudAutoSync.Value > 86400)
                {
                    MessageBox.Show("Auto Sync Interval must be between 60 and 86400 seconds(1 minute~24 hours)", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var region = new Rectangle((int)nudLeft.Value, (int)nudTop.Value, (int)nudWidth.Value, (int)nudHeight.Value);
                double threshold = (double)nudThreshold.Value;
                int interval = (int)nudInterval.Value;

                _overlay = new OverlayForm(region);
                _overlay.Show();

                _monitor = new ScreenMonitor(region, threshold, interval, _dbLogger, Log, CloudLogAsync, _tank, AutoProcessEDF);
                _monitor.Start();
                StartAutoSync();
                btnStart.Text = "Stop Monitoring";
                var startMsg = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " - Monitoring started.";
                Log(startMsg);
                File.AppendAllText("log.txt", startMsg + Environment.NewLine);
            }
        }

        private void BtnImport_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "EDF files (*.edf)|*.edf";
                ofd.Title = "Select an EDF file to import";

                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    string filePath = ofd.FileName;
                    // string oldFolder = Path.Combine(Path.GetDirectoryName(filePath), "old_edf"); // e.g., same folder/old_edf
                    string oldFolder = _oldEdfFolder;

                    // use tank value from UI
                    string tank = txtTank.Text;

                    try
                    {
                        var uploader = new EDFUploader(cloudIp, cloudDb, cloudSchema, cloudUser, cloudPass);
                        bool success = uploader.InsertEDFToDb(filePath, oldFolder, _tank);

                        if (success)
                        {
                            Log($"EDF file imported successfully: {Path.GetFileName(filePath)}");
                        }
                        else
                        {
                            Log($"Failed to import EDF file: {Path.GetFileName(filePath)}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Error importing EDF: {ex.Message}");
                    }
                }
            }
        }

        private void BtnEdf_Click(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Select EDF folder";
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    _edfFolder = dlg.SelectedPath;
                    txtEdf.Text = _edfFolder;
                }
            }
        }

        private void BtnOldEdf_Click(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Select processed EDF folder";
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    _oldEdfFolder = dlg.SelectedPath;
                    txtOldEdf.Text = _oldEdfFolder;
                }
            }
        }



        private async Task CloudLogAsync(string timestamp, bool machineOn, string tank)
        {
            try
            {
                string connString = $"Data Source={cloudIp};Initial Catalog={cloudDb};User ID={cloudUser};Password={cloudPass};Connect Timeout=3";
                using (var con = new SqlConnection(connString))
                {
                    await con.OpenAsync();
                    string sql = $"INSERT INTO [{cloudSchema}].[CHEMICAL_TANK_STATUS] (TANK, DATA_TYPE, DATA_VALUE, DATA_TIME) VALUES (@t, @dt, @p, @d)";
                    using (var cmd = new SqlCommand(sql, con))
                    {
                        cmd.Parameters.AddWithValue("@t", tank);
                        cmd.Parameters.AddWithValue("@dt", "power");
                        cmd.Parameters.AddWithValue("@p", machineOn ? 1.0 : 0.0);
                        cmd.Parameters.AddWithValue("@d", timestamp);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    // mark local as synced
                    using (var lc = new SQLiteConnection($"Data Source={_dbLogger.DbFile};Version=3;"))
                    {
                        lc.Open();
                        using (var u = lc.CreateCommand())
                        {
                            u.CommandText = "UPDATE local SET SYNCED = 1 WHERE CREATED_DATE = @d";
                            u.Parameters.AddWithValue("@d", timestamp);
                            u.ExecuteNonQuery();
                        }
                    }

                    UpdateCloudStatus(true);
                }
            }
            catch
            {
                UpdateCloudStatus(false);
            }
        }

        private void UpdateCloudStatus(bool connected)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<bool>(UpdateCloudStatus), connected);
                return;
            }
            pnlCloud.BackColor = connected ? Color.Green : Color.Red;
        }

        private void BtnSync_Click(object sender, EventArgs e)
        {
            Task.Run(() => SyncData());
        }

        private void SyncData()
        {
            try
            {
                string connString = $"Data Source={cloudIp};Initial Catalog={cloudDb};User ID={cloudUser};Password={cloudPass};Connect Timeout=3";
                using (var con = new SqlConnection(connString))
                {
                    con.Open();
                    UpdateCloudStatus(true);

                    using (var lc = new SQLiteConnection($"Data Source={_dbLogger.DbFile};Version=3;"))
                    {
                        lc.Open();
                        using (var cmd = lc.CreateCommand())
                        {
                            cmd.CommandText = "SELECT ID, TANK, CREATED_DATE, DATA_TYPE, DATA_VALUE FROM local WHERE SYNCED = 0";
                            using (var rdr = cmd.ExecuteReader())
                            {
                                int synced = 0;
                                while (rdr.Read())
                                {
                                    int id = rdr.GetInt32(0);
                                    string tank = rdr.GetString(1);
                                    string created = rdr.GetString(2);
                                    double power = 0.0;
                                    if (!rdr.IsDBNull(4))
                                    {
                                        try
                                        {
                                            power = Convert.ToDouble(rdr.GetValue(4));
                                        }
                                        catch
                                        {
                                            power = 0.0;
                                        }
                                    }
                                    
                                    try
                                    {
                                        using (var insert = new SqlCommand($"INSERT INTO [{cloudSchema}].[CHEMICAL_TANK_STATUS] (TANK, DATA_TYPE, DATA_VALUE, DATA_TIME) VALUES (@t, @dt, @p,@d)", con))
                                        {
                                            insert.Parameters.AddWithValue("@t", tank);
                                            insert.Parameters.AddWithValue("@dt", "power");
                                            insert.Parameters.AddWithValue("@p", power);
                                            insert.Parameters.AddWithValue("@d", created);
                                            insert.ExecuteNonQuery();
                                        }

                                        using (var up = lc.CreateCommand())
                                        {
                                            up.CommandText = "UPDATE local SET SYNCED = 1 WHERE ID = @id";
                                            up.Parameters.AddWithValue("@id", id);
                                            up.ExecuteNonQuery();
                                        }
                                        synced++;
                                    }
                                    catch (Exception ex)
                                    {
                                        Log($"Failed to sync ID {id}: {ex.Message}");
                                    }
                                }
                                lblSyncStatus.Invoke(new Action(() => lblSyncStatus.Text = synced == 0 ? "Sync: No new data" : $"Last sync: {DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
                                Log($"Synced {synced} rows to cloud.");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                UpdateCloudStatus(false);
                lblSyncStatus.Invoke(new Action(() => lblSyncStatus.Text = "Not connected!"));
                Log("Cloud sync failed: " + ex.Message);
            }
        }

        private void StartAutoSync()
        {
            if (_autoSyncRunning) return;
            _autoSyncRunning = true;
            Task.Run(async () =>
            {
                while (_autoSyncRunning)
                {
                    await Task.Delay((int)nudAutoSync.Value * 1000);
                    if (!_autoSyncRunning) break;
                    await Task.Run(() => SyncData());
                }
            });
        }

        private void StopAutoSync()
        {
            _autoSyncRunning = false;
        }

        private void Log(string message)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<string>(Log), message);
                return;
            }
            txtLog.AppendText(message + Environment.NewLine);
            txtLog.SelectionStart = txtLog.Text.Length;
            txtLog.ScrollToCaret();
        }
    }
}
