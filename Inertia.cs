using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Magic Trackpad Momentum Engine")]
[assembly: AssemblyDescription("Low-latency momentum scrolling engine for Windows")]
[assembly: AssemblyProduct("Magic Trackpad Engine")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyVersion("3.1.3.0")]

class MomentumMouse
{
    // ==========================================
    // 基礎物理參數
    // ==========================================
    public static double HighSpeedFriction = 0.950; 
    public static double LowSpeedFriction = 0.750;  
    public static double MinVelocity = 1.2;        
    public static double MinSwipeDistance = 10.0;  
    public static int TickRateMs = 10; 
    
    // ==========================================
    // 進階系統參數
    // ==========================================
    public static int EdgeMode = 3; 
    public static double BounceFactor = 0.20; 
    public static int FixedBouncePixels = 25; 
    
    public static double CrossMonitorResist = 0.20; 
    public static double YAxisBoost = 0.10;         
    public static string BlacklistApp = "photoshop, illustrator, excel, csgo"; 
    public static bool AutoStart = false;           
    public static bool ShowPreview = true;          

    public static string Lang = "EN"; 
    public static bool IsEngineEnabled = true; 
    public static bool IsDebugMode = false;    

    public static double VirtualX = 0;
    public static double VirtualY = 0;
    public static bool IsVirtualMoving = false;
    public static int DebugFadeAlpha = 0; 
    private static int _fixedBounceStepsLeft = 0;
    private static double _fixedBounceDx = 0;
    private static double _fixedBounceDy = 0;

    public static long LastPhysicalMouseTime = 0; 
    private static string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");

    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int LLMHF_INJECTED = 0x01;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern IntPtr GetModuleHandle(string lpModuleName);
    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    private static IntPtr _hookID = IntPtr.Zero;
    private static POINT _lastPos, _startPos;     
    private static bool _isTouching = false; 
    private static long _lastTime;
    private static double _vx = 0, _vy = 0;
    private static bool _isInertiaRunning = false;
    private static object _lock = new object();
    private static LowLevelMouseProc _proc = HookCallback;
    public static DebugOverlay Overlay;
    
    // 全域語言切換通知事件
    public static Action OnLanguageChanged;

    [STAThread]
    public static void Main()
    {
        LoadConfig();
        ApplyAutoStart();
        Thread monitor = new Thread(InertiaLoop);
        monitor.IsBackground = true;
        monitor.Start();
        
        _hookID = SetHook(_proc);
        Application.Run(new AppContext()); 
        UnhookWindowsHookEx(_hookID);
    }

    public static string L(string en, string zh) { return Lang == "ZH" ? zh : en; }

    public static Icon GenerateAppIcon(bool isActive)
    {
        Bitmap bmp = new Bitmap(64, 64);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(new SolidBrush(Color.FromArgb(40, 40, 43)), 2, 2, 60, 60);
            using (Pen p = new Pen(Color.FromArgb(220, 220, 220), 4)) { p.LineJoin = LineJoin.Round; g.DrawRectangle(p, 14, 24, 36, 22); }
            using (Pen p = new Pen(isActive ? Color.FromArgb(0, 120, 215) : Color.Gray, 4))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawArc(p, 22, 12, 20, 20, 180, 120);
                g.FillEllipse(new SolidBrush(isActive ? Color.FromArgb(0, 120, 215) : Color.Gray), 40, 8, 6, 6);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string[] lines = File.ReadAllLines(ConfigPath);
                foreach (string line in lines)
                {
                    if (line.Trim().StartsWith(";")) continue;
                    string[] p = line.Split('=');
                    if (p.Length == 2)
                    {
                        string k = p[0].Trim(); 
                        string v = p[1].Trim();
                        double num; 
                        
                        if (k == "Language") Lang = v;
                        else if (k == "Enabled") IsEngineEnabled = (v == "1");
                        else if (k == "AutoStart") AutoStart = (v == "1");
                        else if (k == "ShowPreview") ShowPreview = (v == "1");
                        else if (k == "Blacklist") BlacklistApp = v;
                        else if (double.TryParse(v, out num))
                        {
                            if (k == "HighSpeed") HighSpeedFriction = num;
                            else if (k == "LowSpeed") LowSpeedFriction = num;
                            else if (k == "Velocity") MinVelocity = num;
                            else if (k == "Distance") MinSwipeDistance = num;
                            else if (k == "Tick") TickRateMs = (int)num;
                            else if (k == "EdgeMode") EdgeMode = (int)num;
                            else if (k == "Bounce") BounceFactor = num;
                            else if (k == "FixedBounce") FixedBouncePixels = (int)num;
                            else if (k == "CrossResist") CrossMonitorResist = num;
                            else if (k == "YAxisBoost") YAxisBoost = num;
                        }
                    }
                }
            }
        } catch { }
    }

    public static void SaveConfig()
    {
        try {
            string content = string.Format("; Trackpad Momentum Engine Settings\r\nLanguage={0}\r\nEnabled={1}\r\nAutoStart={2}\r\nShowPreview={3}\r\nBlacklist={4}\r\nHighSpeed={5:F3}\r\nLowSpeed={6:F3}\r\nVelocity={7:F1}\r\nDistance={8:F1}\r\nTick={9}\r\nEdgeMode={10}\r\nBounce={11:F2}\r\nFixedBounce={12}\r\nCrossResist={13:F2}\r\nYAxisBoost={14:F2}\r\n", 
                Lang, IsEngineEnabled ? "1" : "0", AutoStart ? "1" : "0", ShowPreview ? "1" : "0", BlacklistApp, 
                HighSpeedFriction, LowSpeedFriction, MinVelocity, MinSwipeDistance, TickRateMs, 
                EdgeMode, BounceFactor, FixedBouncePixels, CrossMonitorResist, YAxisBoost);
            File.WriteAllText(ConfigPath, content);
        } catch { }
    }

    public static void ApplyAutoStart()
    {
        try {
            RegistryKey rk = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            if (AutoStart) rk.SetValue("MagicTrackpadEngine", Application.ExecutablePath);
            else rk.DeleteValue("MagicTrackpadEngine", false);
        } catch { }
    }

    private static IntPtr SetHook(LowLevelMouseProc proc)
    {
        using (Process p = Process.GetCurrentProcess()) using (ProcessModule m = p.MainModule)
            return SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(m.ModuleName), 0);
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEMOVE)
            {
                MSLLHOOKSTRUCT hookStruct = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                if ((hookStruct.flags & LLMHF_INJECTED) == 0)
                {
                    lock (_lock)
                    {
                        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                        if (!IsEngineEnabled || now - LastPhysicalMouseTime < 100)
                        {
                            _isInertiaRunning = false; _isTouching = false; _vx = 0; _vy = 0; _fixedBounceStepsLeft = 0;
                            _lastTime = now; _lastPos = hookStruct.pt;
                        }
                        else
                        {
                            _isInertiaRunning = false; _fixedBounceStepsLeft = 0;
                            if (now - _lastTime > 150) { _startPos = hookStruct.pt; _isTouching = true; }
                            if (_lastTime > 0)
                            {
                                double dt = now - _lastTime;
                                if (dt > 0) { _vx = (hookStruct.pt.x - _lastPos.x) / dt; _vy = (hookStruct.pt.y - _lastPos.y) / dt; }
                            }
                            _lastPos = hookStruct.pt; _lastTime = now;
                        }
                    }
                }
            }
            else if (nCode >= 0) lock (_lock) { _isInertiaRunning = false; _isTouching = false; _vx = 0; _vy = 0; _fixedBounceStepsLeft = 0; }
        } catch { }
        return CallNextHookEx(_hookID, nCode, wParam, lParam);
    }

    private static void InertiaLoop()
    {
        while (true)
        {
            try 
            {
                Thread.Sleep(TickRateMs);
                if (!IsEngineEnabled) { IsVirtualMoving = false; continue; }

                lock (_lock)
                {
                    long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                    if (_isTouching && now - _lastTime > 30)
                    {
                        _isTouching = false; 
                        double dx = _lastPos.x - _startPos.x;
                        double dy = _lastPos.y - _startPos.y;
                        double swipeDistance = Math.Sqrt((dx * dx) + (dy * dy));
                        double speed = Math.Sqrt((_vx * _vx) + (_vy * _vy));

                        if (speed > MinVelocity && swipeDistance > MinSwipeDistance) 
                        {
                            bool isBlocked = false;
                            IntPtr hWnd = GetForegroundWindow();
                            if (hWnd != IntPtr.Zero && !string.IsNullOrEmpty(BlacklistApp))
                            {
                                uint pid;
                                GetWindowThreadProcessId(hWnd, out pid);
                                try {
                                    Process proc = Process.GetProcessById((int)pid);
                                    string pName = proc.ProcessName.ToLower();
                                    string[] blocks = BlacklistApp.ToLower().Split(new char[]{',', ' ', ';'}, StringSplitOptions.RemoveEmptyEntries);
                                    foreach(string b in blocks) {
                                        if (pName.Contains(b)) { isBlocked = true; break; }
                                    }
                                } catch { }
                            }

                            if (!isBlocked)
                            {
                                _isInertiaRunning = true; _fixedBounceStepsLeft = 0;
                                if (IsDebugMode) { VirtualX = _lastPos.x; VirtualY = _lastPos.y; IsVirtualMoving = true; DebugFadeAlpha = 220; }
                            }
                            else { _vx = 0; _vy = 0; }
                        }
                        else { _vx = 0; _vy = 0; IsVirtualMoving = false; }
                    }

                    if (_fixedBounceStepsLeft > 0)
                    {
                        _fixedBounceStepsLeft--;
                        if (IsDebugMode)
                        {
                            VirtualX += _fixedBounceDx; VirtualY += _fixedBounceDy;
                            IsVirtualMoving = true; DebugFadeAlpha = 220;
                        }
                        else
                        {
                            INPUT[] inputs = new INPUT[1];
                            inputs[0].type = 0;
                            inputs[0].mi.dx = (int)Math.Round(_fixedBounceDx);
                            inputs[0].mi.dy = (int)Math.Round(_fixedBounceDy);
                            inputs[0].mi.dwFlags = 0x0001;
                            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
                        }
                        if (_fixedBounceStepsLeft == 0) { _isInertiaRunning = false; _vx = 0; _vy = 0; IsVirtualMoving = false; }
                        continue;
                    }

                    if (_isInertiaRunning)
                    {
                        double speed = Math.Sqrt((_vx * _vx) + (_vy * _vy));
                        double currentFriction = (speed > 12.0) ? HighSpeedFriction : 
                                                 (speed > 2.0) ? LowSpeedFriction + (HighSpeedFriction - LowSpeedFriction) * ((speed - 2.0) / 10.0) : 
                                                 LowSpeedFriction;

                        _vx *= currentFriction; 
                        double yFriction = currentFriction + ((1.0 - currentFriction) * YAxisBoost);
                        _vy *= yFriction;

                        if (Math.Abs(_vx) < 0.1 && Math.Abs(_vy) < 0.1)
                        {
                            _isInertiaRunning = false; _vx = 0; _vy = 0; IsVirtualMoving = false; continue;
                        }

                        int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
                        int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
                        int right = left + GetSystemMetrics(SM_CXVIRTUALSCREEN);
                        int bottom = top + GetSystemMetrics(SM_CYVIRTUALSCREEN);

                        POINT currentPt; GetCursorPos(out currentPt);
                        Point ptCurr = new Point((int)currentPt.x, (int)currentPt.y);
                        Point ptNext = new Point((int)(currentPt.x + _vx * TickRateMs), (int)(currentPt.y + _vy * TickRateMs));
                        
                        Screen s1 = Screen.FromPoint(ptCurr);
                        Screen s2 = Screen.FromPoint(ptNext);
                        if (s1.DeviceName != s2.DeviceName)
                        {
                            _vx *= (1.0 - CrossMonitorResist);
                            _vy *= (1.0 - CrossMonitorResist);
                        }

                        if (IsDebugMode)
                        {
                            DebugFadeAlpha = 220; 
                            VirtualX += _vx * TickRateMs; VirtualY += _vy * TickRateMs;
                            
                            bool hitLeft = VirtualX <= left; bool hitRight = VirtualX >= right - 1;
                            bool hitTop = VirtualY <= top; bool hitBottom = VirtualY >= bottom - 1;

                            if (hitLeft || hitRight || hitTop || hitBottom)
                            {
                                if (EdgeMode == 0) { _vx = 0; _vy = 0; }
                                else if (EdgeMode == 1) { if (hitLeft || hitRight) _vx = 0; if (hitTop || hitBottom) _vy = 0; }
                                else if (EdgeMode == 2) { if (hitLeft || hitRight) _vx = -_vx * BounceFactor; if (hitTop || hitBottom) _vy = -_vy * BounceFactor; }
                                else if (EdgeMode == 3)
                                {
                                    int signX = hitLeft ? 1 : (hitRight ? -1 : 0); int signY = hitTop ? 1 : (hitBottom ? -1 : 0);
                                    _fixedBounceStepsLeft = 5; _fixedBounceDx = (signX * FixedBouncePixels) / 5.0; _fixedBounceDy = (signY * FixedBouncePixels) / 5.0; _vx = 0; _vy = 0;
                                }
                                VirtualX = Math.Max(left, Math.Min(VirtualX, right - 1)); VirtualY = Math.Max(top, Math.Min(VirtualY, bottom - 1));
                            }
                            IsVirtualMoving = true;
                        }
                        else
                        {
                            double nextX = currentPt.x + (_vx * TickRateMs); double nextY = currentPt.y + (_vy * TickRateMs);
                            bool hitLeft = nextX <= left; bool hitRight = nextX >= right - 1;
                            bool hitTop = nextY <= top; bool hitBottom = nextY >= bottom - 1;

                            if (hitLeft || hitRight || hitTop || hitBottom)
                            {
                                if (EdgeMode == 0) { _vx = 0; _vy = 0; _isInertiaRunning = false; continue; }
                                else if (EdgeMode == 1) { if (hitLeft || hitRight) _vx = 0; if (hitTop || hitBottom) _vy = 0; if (_vx == 0 && _vy == 0) { _isInertiaRunning = false; continue; } }
                                else if (EdgeMode == 2) 
                                { 
                                    if (hitLeft || hitRight) _vx = -_vx * BounceFactor; 
                                    if (hitTop || hitBottom) _vy = -_vy * BounceFactor; 
                                    if (Math.Abs(_vx) < 0.2 && Math.Abs(_vy) < 0.2) { _isInertiaRunning = false; _vx = 0; _vy = 0; continue; }
                                }
                                else if (EdgeMode == 3)
                                {
                                    int signX = hitLeft ? 1 : (hitRight ? -1 : 0); int signY = hitTop ? 1 : (hitBottom ? -1 : 0);
                                    _fixedBounceStepsLeft = 5; _fixedBounceDx = (signX * FixedBouncePixels) / 5.0; _fixedBounceDy = (signY * FixedBouncePixels) / 5.0; _vx = 0; _vy = 0; continue;
                                }
                            }
                            INPUT[] inputs = new INPUT[1];
                            inputs[0].type = 0; 
                            inputs[0].mi.dx = (int)Math.Round(_vx * TickRateMs); inputs[0].mi.dy = (int)Math.Round(_vy * TickRateMs);
                            inputs[0].mi.dwFlags = 0x0001; 
                            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
                        }
                    }
                }
            } catch { } 
        }
    }
}

// ==========================================
// 視覺化幽靈軌跡圖層
// ==========================================
class DebugOverlay : Form
{
    private System.Windows.Forms.Timer renderTimer;
    protected override bool ShowWithoutActivation { get { return true; } }

    public DebugOverlay()
    {
        this.DoubleBuffered = true; this.FormBorderStyle = FormBorderStyle.None; this.TopMost = true;
        this.BackColor = Color.Magenta; this.TransparencyKey = Color.Magenta; this.ShowInTaskbar = false;
        
        int left = MomentumMouse.GetSystemMetrics(MomentumMouse.SM_XVIRTUALSCREEN);
        int top = MomentumMouse.GetSystemMetrics(MomentumMouse.SM_YVIRTUALSCREEN);
        int width = MomentumMouse.GetSystemMetrics(MomentumMouse.SM_CXVIRTUALSCREEN);
        int height = MomentumMouse.GetSystemMetrics(MomentumMouse.SM_CYVIRTUALSCREEN);
        this.Bounds = new Rectangle(left, top, width, height);

        renderTimer = new System.Windows.Forms.Timer();
        renderTimer.Interval = 16;
        renderTimer.Tick += (s, e) => { 
            if (!MomentumMouse.IsDebugMode || !MomentumMouse.ShowPreview) 
            {
                if (MomentumMouse.DebugFadeAlpha > 0) 
                {
                    MomentumMouse.DebugFadeAlpha = 0;
                    this.Invalidate();
                }
                return;
            }

            if (!MomentumMouse.IsVirtualMoving && MomentumMouse.DebugFadeAlpha > 0)
            {
                MomentumMouse.DebugFadeAlpha -= 4; 
                if (MomentumMouse.DebugFadeAlpha < 0) MomentumMouse.DebugFadeAlpha = 0;
                this.Invalidate();
            }
            else if (MomentumMouse.IsVirtualMoving)
            {
                this.Invalidate();
            }
        };
        renderTimer.Start();
    }
    
    protected override CreateParams CreateParams { get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x20; return cp; } }
    
    protected override void OnPaint(PaintEventArgs e)
    {
        int safeAlpha = MomentumMouse.DebugFadeAlpha;
        if (safeAlpha < 0) safeAlpha = 0;
        
        int displayAlpha = Math.Min(safeAlpha, 120);

        if (MomentumMouse.IsDebugMode && MomentumMouse.ShowPreview && displayAlpha > 0)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            MomentumMouse.POINT pt; MomentumMouse.GetCursorPos(out pt);
            
            float realX = pt.x - this.Left; float realY = pt.y - this.Top;
            float virtX = (float)MomentumMouse.VirtualX - this.Left; float virtY = (float)MomentumMouse.VirtualY - this.Top;

            using (Pen linePen = new Pen(Color.FromArgb(displayAlpha, 220, 220, 220), 2))
            {
                linePen.DashStyle = DashStyle.Dash; e.Graphics.DrawLine(linePen, realX, realY, virtX, virtY);
            }
            using (SolidBrush bReal = new SolidBrush(Color.FromArgb(displayAlpha, 255, 235, 59)))
            {
                e.Graphics.FillEllipse(bReal, realX - 15, realY - 15, 30, 30);
            }
            using (SolidBrush bVirtual = new SolidBrush(Color.FromArgb(displayAlpha, 0, 255, 255)))
            {
                e.Graphics.FillEllipse(bVirtual, virtX - 25, virtY - 25, 50, 50);
            }
        }
    }
}

// ==========================================
// 現代化黑名單管理視窗
// ==========================================
class BlacklistForm : Form
{
    ListBox listBox;
    TextBox txtAdd;
    Button btnAdd, btnRemove, btnClose;
    
    public BlacklistForm()
    {
        this.Text = MomentumMouse.L("Manage Blacklist", "管理軟體黑名單");
        this.Size = new Size(320, 400);
        this.StartPosition = FormStartPosition.CenterParent;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.BackColor = Color.FromArgb(40, 40, 40);
        this.ForeColor = Color.White;
        this.ShowIcon = false;

        Label lbl = new Label() { Text = MomentumMouse.L("Ignore inertia in these apps:", "在以下程式中完全停用慣性："), Location = new Point(15, 15), AutoSize = true, Font = new Font("Segoe UI", 9) };
        this.Controls.Add(lbl);

        listBox = new ListBox() { Location = new Point(15, 40), Size = new Size(270, 230), BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White, Font = new Font("Segoe UI", 10), BorderStyle = BorderStyle.None };
        string[] apps = MomentumMouse.BlacklistApp.Split(new char[]{',', ';'}, StringSplitOptions.RemoveEmptyEntries);
        foreach (string app in apps) { string t = app.Trim(); if (t.Length > 0) listBox.Items.Add(t); }
        this.Controls.Add(listBox);

        txtAdd = new TextBox() { Location = new Point(15, 280), Size = new Size(185, 25), BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White, Font = new Font("Segoe UI", 10), BorderStyle = BorderStyle.FixedSingle };
        this.Controls.Add(txtAdd);

        btnAdd = new Button() { Text = "+", Location = new Point(210, 280), Size = new Size(35, 25), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 120, 215), Cursor = Cursors.Hand };
        btnAdd.FlatAppearance.BorderSize = 0;
        btnAdd.Click += (s, e) => {
            string nv = txtAdd.Text.Trim().ToLower();
            if (nv.Length > 0 && !listBox.Items.Contains(nv)) { listBox.Items.Add(nv); txtAdd.Text = ""; }
        };
        this.Controls.Add(btnAdd);

        btnRemove = new Button() { Text = "-", Location = new Point(250, 280), Size = new Size(35, 25), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(200, 50, 50), Cursor = Cursors.Hand };
        btnRemove.FlatAppearance.BorderSize = 0;
        btnRemove.Click += (s, e) => { if (listBox.SelectedIndex != -1) listBox.Items.RemoveAt(listBox.SelectedIndex); };
        this.Controls.Add(btnRemove);

        btnClose = new Button() { Text = MomentumMouse.L("Done", "完成"), Location = new Point(15, 315), Size = new Size(270, 30), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(80, 80, 80), Cursor = Cursors.Hand };
        btnClose.FlatAppearance.BorderSize = 0;
        btnClose.Click += (s, e) => { this.Close(); };
        this.Controls.Add(btnClose);

        this.FormClosing += (s, e) => {
            string res = "";
            foreach (var item in listBox.Items) { res += item.ToString() + ", "; }
            if (res.EndsWith(", ")) res = res.Substring(0, res.Length - 2);
            MomentumMouse.BlacklistApp = res;
        };
    }
}

// ==========================================
// 雙欄式控制面板 (Dual-Column Settings Form)
// ==========================================
class SettingsForm : Form
{
    TrackBar tbHigh, tbLow, tbDist, tbVel, tbTick, tbEdgeParam, tbCrossResist, tbYAxisBoost;
    Label lblHigh, lblLow, lblDist, lblVel, lblTick, lblEdgeParam, lblCrossResist, lblYAxisBoost;
    Label title, subtitle1, subtitle2, lblBlacklistStatus;
    CheckBox chkAutoStart, chkShowPreview;
    
    Button btnSys, btnSave, btnLang, btnEdgeMode, btnManageBlacklist, btnDefault;
    
    ToolTip toolTip;
    
    Color bgDark = Color.FromArgb(30, 30, 30);
    Color fgWhite = Color.FromArgb(240, 240, 240);
    Color fgGray = Color.FromArgb(170, 170, 170);
    Color accentBlue = Color.FromArgb(0, 120, 215);
    Color warnYellow = Color.FromArgb(255, 215, 0);

    public SettingsForm()
    {
        this.Size = new Size(820, 560); 
        this.StartPosition = FormStartPosition.CenterScreen;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.BackColor = bgDark;
        this.ForeColor = fgWhite;
        this.Icon = MomentumMouse.GenerateAppIcon(true);

        toolTip = new ToolTip() { AutoPopDelay = 15000, InitialDelay = 300, ReshowDelay = 200 };

        title = new Label() { Location = new Point(20, 15), AutoSize = true, Font = new Font("Segoe UI", 12, FontStyle.Bold) };
        this.Controls.Add(title);

        // 將 CheckBox 往右移，防止被標題蓋住
        chkShowPreview = new CheckBox() { Location = new Point(280, 18), AutoSize = true, Font = new Font("Segoe UI", 9), ForeColor = warnYellow, Cursor = Cursors.Hand, Checked = MomentumMouse.ShowPreview };
        chkShowPreview.CheckedChanged += (s, e) => { 
            MomentumMouse.ShowPreview = chkShowPreview.Checked; 
            MomentumMouse.DebugFadeAlpha = 0;
            if (MomentumMouse.Overlay != null) MomentumMouse.Overlay.Invalidate();
            MomentumMouse.SaveConfig(); 
        };
        this.Controls.Add(chkShowPreview);
        
        btnLang = CreateFlatButton("", 720, 12, 50, Color.FromArgb(60, 60, 60));
        btnLang.Click += (s, e) => { 
            MomentumMouse.Lang = (MomentumMouse.Lang == "EN") ? "ZH" : "EN"; 
            MomentumMouse.SaveConfig(); 
            ApplyLanguage(); 
            if (MomentumMouse.OnLanguageChanged != null) MomentumMouse.OnLanguageChanged(); // 觸發系統匣語言更新
        };

        subtitle1 = new Label() { Location = new Point(20, 55), AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), ForeColor = accentBlue };
        this.Controls.Add(subtitle1);
        subtitle2 = new Label() { Location = new Point(415, 55), AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), ForeColor = accentBlue };
        this.Controls.Add(subtitle2);

        // ================= COL 1 =================
        int col1X = 20;
        lblHigh = CreateLabel(col1X, 90); tbHigh = CreateTrackBar(col1X-5, 110, 900, 999, (int)(MomentumMouse.HighSpeedFriction * 1000));
        CreateResetButton(col1X+330, 105, () => { tbHigh.Value = 950; });

        lblLow = CreateLabel(col1X, 160); tbLow = CreateTrackBar(col1X-5, 180, 500, 950, (int)(MomentumMouse.LowSpeedFriction * 1000));
        CreateResetButton(col1X+330, 175, () => { tbLow.Value = 750; });

        lblVel = CreateLabel(col1X, 230); tbVel = CreateTrackBar(col1X-5, 250, 0, 50, (int)(MomentumMouse.MinVelocity * 10));
        CreateResetButton(col1X+330, 245, () => { tbVel.Value = 12; });

        lblDist = CreateLabel(col1X, 300); tbDist = CreateTrackBar(col1X-5, 320, 0, 50, (int)MomentumMouse.MinSwipeDistance);
        CreateResetButton(col1X+330, 315, () => { tbDist.Value = 10; });

        lblTick = CreateLabel(col1X, 370); tbTick = CreateTrackBar(col1X-5, 390, 5, 30, MomentumMouse.TickRateMs);
        CreateResetButton(col1X+330, 385, () => { tbTick.Value = 10; });

        // ================= COL 2 =================
        int col2X = 415;
        btnEdgeMode = CreateFlatButton("", col2X, 90, 130, Color.FromArgb(60, 60, 60));
        btnEdgeMode.Click += (s, e) => { MomentumMouse.EdgeMode = (MomentumMouse.EdgeMode + 1) % 4; SyncEdgeSlider(); ApplyLanguage(); };
        
        lblEdgeParam = CreateLabel(col2X+140, 85);
        tbEdgeParam = CreateTrackBar(col2X+135, 110, 0, 100, 25); tbEdgeParam.Width = 190;
        CreateResetButton(col2X+330, 105, () => { if (MomentumMouse.EdgeMode == 2) tbEdgeParam.Value = 20; else if (MomentumMouse.EdgeMode == 3) tbEdgeParam.Value = 25; UpdateLabels(); });

        lblCrossResist = CreateLabel(col2X, 160); tbCrossResist = CreateTrackBar(col2X-5, 180, 0, 100, (int)(MomentumMouse.CrossMonitorResist * 100));
        CreateResetButton(col2X+330, 175, () => { tbCrossResist.Value = 20; });

        lblYAxisBoost = CreateLabel(col2X, 230); tbYAxisBoost = CreateTrackBar(col2X-5, 250, 0, 50, (int)(MomentumMouse.YAxisBoost * 100));
        CreateResetButton(col2X+330, 245, () => { tbYAxisBoost.Value = 10; });

        lblBlacklistStatus = CreateLabel(col2X, 310);
        btnManageBlacklist = CreateFlatButton("", col2X, 330, 160, Color.FromArgb(60, 60, 60));
        btnManageBlacklist.Click += (s, e) => { 
            new BlacklistForm().ShowDialog(this);
            UpdateLabels();
        };

        chkAutoStart = new CheckBox() { Location = new Point(col2X, 390), AutoSize = true, Font = new Font("Segoe UI", 9), ForeColor = fgWhite, Cursor = Cursors.Hand, Checked = MomentumMouse.AutoStart };
        this.Controls.Add(chkAutoStart);

        // ================= BOTTOM =================
        btnDefault = CreateFlatButton("", 20, 465, 100, Color.FromArgb(60, 60, 60));
        btnDefault.Click += (s, e) => { 
            tbHigh.Value = 950; tbLow.Value = 750; tbVel.Value = 12; tbDist.Value = 10; tbTick.Value = 10; 
            MomentumMouse.EdgeMode = 3; tbEdgeParam.Value = 25;
            tbCrossResist.Value = 20; tbYAxisBoost.Value = 10; 
            chkAutoStart.Checked = false; chkShowPreview.Checked = true;
            MomentumMouse.ShowPreview = true;
            MomentumMouse.BlacklistApp = "photoshop, illustrator, excel, csgo";
            SyncEdgeSlider(); ApplyLanguage(); 
        };

        btnSys = CreateFlatButton("", 130, 465, 120, Color.FromArgb(60, 60, 60));
        btnSys.Click += (s, e) => { Process.Start(new ProcessStartInfo("ms-settings:devices-touchpad") { UseShellExecute = true }); };
        
        btnSave = CreateFlatButton("", 570, 465, 200, accentBlue);
        btnSave.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        btnSave.Click += (s, e) => {
            MomentumMouse.HighSpeedFriction = tbHigh.Value / 1000.0; MomentumMouse.LowSpeedFriction = tbLow.Value / 1000.0;
            MomentumMouse.MinVelocity = tbVel.Value / 10.0; MomentumMouse.MinSwipeDistance = tbDist.Value; 
            MomentumMouse.TickRateMs = tbTick.Value; 
            if (MomentumMouse.EdgeMode == 2) MomentumMouse.BounceFactor = tbEdgeParam.Value / 100.0;
            else if (MomentumMouse.EdgeMode == 3) MomentumMouse.FixedBouncePixels = tbEdgeParam.Value;
            MomentumMouse.CrossMonitorResist = tbCrossResist.Value / 100.0;
            MomentumMouse.YAxisBoost = tbYAxisBoost.Value / 100.0;
            MomentumMouse.AutoStart = chkAutoStart.Checked;
            
            MomentumMouse.ApplyAutoStart();
            MomentumMouse.SaveConfig(); 
            this.Hide(); 
        };

        SyncEdgeSlider();
        ApplyLanguage();
    }

    private void SyncEdgeSlider()
    {
        if (MomentumMouse.EdgeMode == 2) { tbEdgeParam.Minimum = 1; tbEdgeParam.Maximum = 100; tbEdgeParam.Value = Math.Max(1, Math.Min(100, (int)(MomentumMouse.BounceFactor * 100))); }
        else if (MomentumMouse.EdgeMode == 3) { tbEdgeParam.Minimum = 5; tbEdgeParam.Maximum = 120; tbEdgeParam.Value = Math.Max(5, Math.Min(120, MomentumMouse.FixedBouncePixels)); }
    }

    private Label CreateLabel(int x, int y) { Label lbl = new Label() { Location = new Point(x, y), AutoSize = true, Font = new Font("Segoe UI", 9), ForeColor = fgGray }; this.Controls.Add(lbl); return lbl; }
    private TrackBar CreateTrackBar(int x, int y, int min, int max, int val) { TrackBar tb = new TrackBar() { Location = new Point(x, y), Width = 330, Minimum = min, Maximum = max, Value = val, TickStyle = TickStyle.None }; tb.Scroll += (s, e) => UpdateLabels(); this.Controls.Add(tb); return tb; }
    private Button CreateFlatButton(string text, int x, int y, int width, Color bgColor) { Button btn = new Button() { Text = text, Location = new Point(x, y), Size = new Size(width, 35), FlatStyle = FlatStyle.Flat, BackColor = bgColor, ForeColor = Color.White, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9) }; btn.FlatAppearance.BorderSize = 0; this.Controls.Add(btn); return btn; }
    private void CreateResetButton(int x, int y, Action onClick) { Button btn = new Button() { Text = "↺", Location = new Point(x, y), Size = new Size(30, 25), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(50,50,50), ForeColor = Color.White, Cursor = Cursors.Hand }; btn.FlatAppearance.BorderSize = 0; btn.Click += (s, e) => { onClick(); ApplyLanguage(); }; this.Controls.Add(btn); }

    private void ApplyLanguage()
    {
        this.Text = MomentumMouse.L("Trackpad Settings", "觸控版慣性設定");
        title.Text = MomentumMouse.L("Dynamics Tuning & Systems", "動態阻尼與系統設定");
        chkShowPreview.Text = MomentumMouse.L("Show Trajectory Preview", "顯示滑動軌跡預覽");
        subtitle1.Text = MomentumMouse.L("Basic Physics (Left)", "基礎物理引擎 (左欄)");
        subtitle2.Text = MomentumMouse.L("Advanced Systems (Right)", "進階與系統設定 (右欄)");
        btnLang.Text = MomentumMouse.L("中文", "EN");
        
        btnManageBlacklist.Text = MomentumMouse.L("Manage Blacklist...", "管理例外黑名單...");
        btnSys.Text = MomentumMouse.L("Win Settings", "Win 觸控設定");
        btnSave.Text = MomentumMouse.L("Save & Apply", "套用並隱藏");
        btnDefault.Text = MomentumMouse.L("Default", "全部還原預設"); 

        string[] modesEN = new string[] { "Hard Stop", "Slide Edge", "Bounce %", "Fixed Rebound" };
        string[] modesZH = new string[] { "撞牆：煞停", "撞牆：滑行", "撞牆：比例", "撞牆：固定" };
        btnEdgeMode.Text = MomentumMouse.L(modesEN[MomentumMouse.EdgeMode], modesZH[MomentumMouse.EdgeMode]);

        chkAutoStart.Text = MomentumMouse.L("Start automatically on Windows boot (Hidden in tray)", "開機自動啟動 (隱藏於系統匣)");

        toolTip.SetToolTip(tbHigh, MomentumMouse.L("Multiplier applied every tick when sliding fast.", "高速滑行時每次更新保留的速度比例。"));
        toolTip.SetToolTip(tbLow, MomentumMouse.L("Multiplier applied when cursor slows down.", "速度降下時的尾段煞車比例，數值愈低煞車愈重。"));
        toolTip.SetToolTip(tbVel, MomentumMouse.L("Minimum speed required to trigger inertia.", "甩開手指瞬間的速度門檻，愈高愈難觸發。"));
        toolTip.SetToolTip(tbDist, MomentumMouse.L("Minimum sliding distance to trigger inertia.", "總滑行距離需大於此像素才會觸發，防止打字誤觸。"));
        toolTip.SetToolTip(tbTick, MomentumMouse.L("Engine calculation frequency (10ms = 100Hz).", "物理引擎運算週期，預設 10ms 最契合藍牙硬體。"));
        toolTip.SetToolTip(tbCrossResist, MomentumMouse.L("Kinetic penalty when cursor crosses dual monitor bounds.", "游標跨越雙螢幕交界時的動能損耗 (0=無感, 100=無法跨越)。"));
        toolTip.SetToolTip(tbYAxisBoost, MomentumMouse.L("Extra momentum retention for vertical (Y) swipes.", "針對上下滑動額外保留的動能比例，彌補人體工學限制。"));

        // 更新控制項顯示隱藏
        bool showSlider = (MomentumMouse.EdgeMode == 2 || MomentumMouse.EdgeMode == 3);
        tbEdgeParam.Visible = showSlider; lblEdgeParam.Visible = showSlider; 
        // 使用迴圈安全隱藏重設按鈕 (避免 index 跑掉)
        foreach(Control c in this.Controls) { if(c is Button && c.Text == "↺" && c.Top == 105 && c.Left > 400) { c.Visible = showSlider; break; } }

        UpdateLabels();
    }

    private void UpdateLabels()
    {
        lblHigh.Text = string.Format(MomentumMouse.L("High Speed Float (Glide multiplier): {0:F3}", "高速滑行 (每次更新保留比例): {0:F3}"), tbHigh.Value / 1000.0);
        lblLow.Text = string.Format(MomentumMouse.L("Low Speed Brake (Sticky finish): {0:F3}", "低速煞車 (重現尾段黏滯感): {0:F3}"), tbLow.Value / 1000.0);
        lblVel.Text = string.Format(MomentumMouse.L("Activation Speed (px/tick): {0:F1}", "啟動初速 (px/每次更新): {0:F1}"), tbVel.Value / 10.0);
        lblDist.Text = string.Format(MomentumMouse.L("Palm Rejection (Ignore < px): {0} px", "防掌觸過濾 (忽略短距離抖動): {0} px"), tbDist.Value);
        lblTick.Text = string.Format(MomentumMouse.L("Engine Tick Rate: {0} ms", "引擎更新率 (平滑度): {0} ms"), tbTick.Value);
        
        lblCrossResist.Text = string.Format(MomentumMouse.L("Multi-Monitor Crossing Resistance: {0}%", "跨螢幕阻力損耗: {0}%"), tbCrossResist.Value);
        lblYAxisBoost.Text = string.Format(MomentumMouse.L("Y-Axis Momentum Boost: {0}%", "Y軸人體工學動能補償: +{0}%"), tbYAxisBoost.Value);

        if (MomentumMouse.EdgeMode == 2) lblEdgeParam.Text = string.Format(MomentumMouse.L("Bounce Force: {0}%", "反彈力道: {0}%"), tbEdgeParam.Value);
        else if (MomentumMouse.EdgeMode == 3) lblEdgeParam.Text = string.Format(MomentumMouse.L("Rebound Dist: {0} px", "固定回彈: {0} px"), tbEdgeParam.Value);

        string[] apps = MomentumMouse.BlacklistApp.Split(new char[]{',', ';'}, StringSplitOptions.RemoveEmptyEntries);
        lblBlacklistStatus.Text = string.Format(MomentumMouse.L("Blacklist Active: Ignoring {0} app(s).", "例外黑名單: 目前已封鎖 {0} 個軟體。"), apps.Length);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        MomentumMouse.IsDebugMode = this.Visible; 
        if (this.Visible) { if (MomentumMouse.Overlay == null) { MomentumMouse.Overlay = new DebugOverlay(); MomentumMouse.Overlay.Show(); } }
        else { if (MomentumMouse.Overlay != null) { MomentumMouse.Overlay.Close(); MomentumMouse.Overlay = null; } }
    }
    protected override void OnFormClosing(FormClosingEventArgs e) { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; this.Hide(); } base.OnFormClosing(e); }
}

// ==========================================
// 系統匣與底層攔截
// ==========================================
class AppContext : ApplicationContext
{
    private NotifyIcon _trayIcon;
    private SettingsForm _settingsForm;
    private Form _rawInputForm;

    [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);
    [DllImport("user32.dll")] private static extern int GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }
    [StructLayout(LayoutKind.Explicit)] struct RAWMOUSE { [FieldOffset(0)] public ushort usFlags; [FieldOffset(4)] public uint ulButtons; [FieldOffset(8)] public uint ulRawButtons; [FieldOffset(12)] public int lLastX; [FieldOffset(16)] public int lLastY; [FieldOffset(20)] public uint ulExtraInformation; }

    public AppContext()
    {
        _settingsForm = new SettingsForm();
        _trayIcon = new NotifyIcon();
        UpdateTrayMenu();
        _trayIcon.Visible = true;
        _trayIcon.DoubleClick += (s, e) => { _settingsForm.Show(); _settingsForm.Activate(); };

        _rawInputForm = new Form();
        _rawInputForm.HandleCreated += (s, e) => {
            RAWINPUTDEVICE[] rid = new RAWINPUTDEVICE[1];
            rid[0].usUsagePage = 0x01; rid[0].usUsage = 0x02; rid[0].dwFlags = 0x00000100; rid[0].hwndTarget = _rawInputForm.Handle;
            RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        };
        _rawInputForm.Visible = false;

        // 綁定語言切換事件
        MomentumMouse.OnLanguageChanged += UpdateTrayMenu;

        Application.AddMessageFilter(new RawInputFilter());
        _settingsForm.Show();
    }

    public void UpdateTrayMenu()
    {
        ContextMenu menu = new ContextMenu();
        menu.MenuItems.Add(MomentumMouse.L("Settings...", "開啟控制面板..."), (s, e) => { _settingsForm.Show(); _settingsForm.Activate(); });
        menu.MenuItems.Add("-"); 
        
        string toggleText = MomentumMouse.IsEngineEnabled ? MomentumMouse.L("Disable Engine", "停用慣性引擎") : MomentumMouse.L("Enable Engine", "啟用慣性引擎");
        MenuItem mnuToggle = new MenuItem(toggleText, (s, e) => { 
            MomentumMouse.IsEngineEnabled = !MomentumMouse.IsEngineEnabled; 
            MomentumMouse.SaveConfig();
            UpdateTrayMenu(); 
        });
        mnuToggle.Checked = MomentumMouse.IsEngineEnabled;
        menu.MenuItems.Add(mnuToggle);
        
        menu.MenuItems.Add("-"); 
        menu.MenuItems.Add(MomentumMouse.L("Exit", "完全退出"), (s, e) => { _trayIcon.Visible = false; Application.Exit(); });
        
        _trayIcon.ContextMenu = menu;
        _trayIcon.Text = MomentumMouse.L("Trackpad Momentum Engine", "觸控版慣性引擎");
        _trayIcon.Icon = MomentumMouse.GenerateAppIcon(MomentumMouse.IsEngineEnabled);
    }

    private class RawInputFilter : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg == 0x00FF)
            {
                uint dataSize = 0;
                uint headerSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
                GetRawInputData(m.LParam, 0x10000003, IntPtr.Zero, ref dataSize, headerSize);
                if (dataSize > 0)
                {
                    IntPtr pData = Marshal.AllocHGlobal((int)dataSize);
                    if (GetRawInputData(m.LParam, 0x10000003, pData, ref dataSize, headerSize) == dataSize)
                    {
                        RAWINPUTHEADER h = (RAWINPUTHEADER)Marshal.PtrToStructure(pData, typeof(RAWINPUTHEADER));
                        if (h.dwType == 0 && h.hDevice != IntPtr.Zero) 
                        {
                            IntPtr pMouse = new IntPtr(pData.ToInt64() + headerSize);
                            RAWMOUSE mouse = (RAWMOUSE)Marshal.PtrToStructure(pMouse, typeof(RAWMOUSE));
                            if (mouse.lLastX != 0 || mouse.lLastY != 0) MomentumMouse.LastPhysicalMouseTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                        }
                    }
                    Marshal.FreeHGlobal(pData);
                }
            }
            return false;
        }
    }
}