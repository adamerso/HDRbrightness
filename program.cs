using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing;
using Microsoft.Win32;

namespace BrightnessTrayApp
{
    // ====================================
    // 1) CONSOLE HELPER: Attach or Allocate Console
    // ====================================
    public static class ConsoleHelper
    {
        private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(uint dwProcessId);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AllocConsole();
    }

    // ====================================
    // 2) ICON LOADER (using ExtractIconEx)
    // ====================================
    public static class IconLoader
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

        public static Icon LoadDllIcon(string dllPath, int iconIndex, bool debug = false)
        {
            IntPtr[] largeIcons = new IntPtr[1];
            IntPtr[] smallIcons = new IntPtr[1];
            uint count = ExtractIconEx(dllPath, iconIndex, largeIcons, smallIcons, 1);
            if (count > 0 && largeIcons[0] != IntPtr.Zero)
            {
                try
                {
                    return Icon.FromHandle(largeIcons[0]);
                }
                catch (Exception ex)
                {
                    if (debug)
                        Console.WriteLine($"Error creating icon handle: {ex.Message}");
                }
            }
            if (debug)
                Console.WriteLine($"Failed to extract icon {iconIndex} from {dllPath}");
            return SystemIcons.Application;
        }
    }

    // ====================================
    // 3) BRIGHTNESS CONTROLLER (using registry for settings)
    // ====================================
    public static class BrightnessController
    {
        public const double MIN_BRIGHTNESS = 1.0;
        public const double STEP = 0.5;
        public const double EPSILON = 0.0001;

        // Registry keys for settings (HKCU\Software\BrightnessTrayApp)
        private const string REGPATH = @"Software\BrightnessTrayApp";
        private const string KEY_BRIGHTNESS = "Brightness";
        private const string KEY_RANGE = "RangeMode";

        // Active range mode ("normal" or "extended")
        public static string ActiveRangeMode { get; set; } = "normal";
        public static double CurrentMaxBrightness => (ActiveRangeMode == "extended" ? 12.0 : 6.0);

        // ------------------ WinAPI imports ------------------
        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibrary(string lpFileName);
        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr hModule, int address);
        [DllImport("user32.dll")]
        static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, EnumMonitorsDelegate lpfnEnum, IntPtr dwData);
        [DllImport("User32.dll", CharSet = CharSet.Auto)]
        public static extern bool GetMonitorInfo(IntPtr hmonitor, [In, Out] MonitorInfo info);
        // -----------------------------------------------------

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void DwmpSDRToHDRBoostPtr(IntPtr monitor, double brightness);

        public delegate bool EnumMonitorsDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int left, top, right, bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto, Pack = 4)]
        public class MonitorInfo
        {
            public int cbSize = Marshal.SizeOf(typeof(MonitorInfo));
            public Rect rcMonitor = new Rect();
            public Rect rcWork = new Rect();
            public int dwFlags = 0;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public char[] szDevice = new char[32];
        }

        public class DisplayInfo
        {
            public IntPtr MonitorHandle { get; set; }
        }

        public static List<DisplayInfo> GetDisplays()
        {
            var list = new List<DisplayInfo>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData) =>
                {
                    var mi = new MonitorInfo();
                    mi.cbSize = Marshal.SizeOf(mi);
                    bool success = GetMonitorInfo(hMonitor, mi);
                    if (success)
                    {
                        list.Add(new DisplayInfo { MonitorHandle = hMonitor });
                    }
                    return true;
                }, IntPtr.Zero);
            return list;
        }

        // Reads settings from registry; if missing, returns default brightness 3.0 and "normal" range.
        public static (double brightness, string rangeMode) ReadSettings()
        {
            double brightness = 3.0;
            string range = "normal";
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(REGPATH))
                {
                    if (key != null)
                    {
                        string? bStr = key.GetValue(KEY_BRIGHTNESS, "3.0") as string;
                        if (bStr != null && double.TryParse(bStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double bVal))
                        {
                            brightness = bVal;
                        }
                        string? mode = key.GetValue(KEY_RANGE, "normal") as string;
                        if (mode == "extended")
                            range = "extended";
                    }
                }
            }
            catch { }
            return (brightness, range);
        }

        // Saves settings to registry.
        public static void SaveSettings(double brightness, string rangeMode)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(REGPATH))
                {
                    key.SetValue(KEY_BRIGHTNESS, brightness.ToString(CultureInfo.InvariantCulture), RegistryValueKind.String);
                    key.SetValue(KEY_RANGE, rangeMode, RegistryValueKind.String);
                }
            }
            catch { }
        }

        // Adjusts brightness based on command ("brighter" or "darker") given current max.
        public static double? AdjustBrightness(double current, string command, double maxBrightness)
        {
            if (current < MIN_BRIGHTNESS)
            {
                if (command.Equals("brighter", StringComparison.OrdinalIgnoreCase))
                    return MIN_BRIGHTNESS + STEP;
                else if (command.Equals("darker", StringComparison.OrdinalIgnoreCase))
                    return null;
            }
            if (current > maxBrightness)
            {
                if (command.Equals("darker", StringComparison.OrdinalIgnoreCase))
                    return maxBrightness - STEP;
                else if (command.Equals("brighter", StringComparison.OrdinalIgnoreCase))
                    return null;
            }
            double newBrightness;
            double mult = current * 2;
            bool isOnStep = Math.Abs(mult - Math.Round(mult)) < EPSILON;
            if (command.Equals("brighter", StringComparison.OrdinalIgnoreCase))
            {
                if (isOnStep)
                    newBrightness = current + STEP;
                else
                    newBrightness = Math.Ceiling(mult) / 2.0;
                if (newBrightness > maxBrightness)
                    newBrightness = maxBrightness;
            }
            else if (command.Equals("darker", StringComparison.OrdinalIgnoreCase))
            {
                if (isOnStep)
                    newBrightness = current - STEP;
                else
                    newBrightness = Math.Floor(mult) / 2.0;
                if (newBrightness < MIN_BRIGHTNESS)
                    newBrightness = MIN_BRIGHTNESS;
            }
            else return null;
            return newBrightness;
        }

        // Sets brightness on ALL monitors.
        public static bool SetBrightness(double brightness, bool debug)
        {
            IntPtr hmodule = LoadLibrary("dwmapi.dll");
            if (hmodule == IntPtr.Zero)
            {
                if (debug) Console.WriteLine("Failed to load dwmapi.dll.");
                return false;
            }
            IntPtr ptrChange = GetProcAddress(hmodule, 171);
            if (ptrChange == IntPtr.Zero)
            {
                if (debug) Console.WriteLine("Function to set brightness (ordinal 171) not found.");
                return false;
            }
            DwmpSDRToHDRBoostPtr changeBrightness = Marshal.GetDelegateForFunctionPointer<DwmpSDRToHDRBoostPtr>(ptrChange);
            var monitors = GetDisplays();
            if (monitors.Count == 0)
            {
                if (debug) Console.WriteLine("No monitors found.");
                return false;
            }
            try
            {
                foreach (var m in monitors)
                {
                    changeBrightness(m.MonitorHandle, brightness);
                }
                SaveSettings(brightness, ActiveRangeMode);
                return true;
            }
            catch (Exception ex)
            {
                if (debug) Console.WriteLine("Error setting brightness: " + ex.Message);
                return false;
            }
        }

        public static void RestartApplication(bool debug)
        {
            // Przy restarcie najpierw należy spróbować odpiąć hotkey i posprzątać.
            Console.WriteLine("Restarting application...");
            Application.Restart();
            Environment.Exit(0);
        }

        public static void IncreaseBrightness(bool debug)
        {
            var (current, rangeMode) = ReadSettings();
            double max = (rangeMode == "extended" ? 12.0 : 6.0);
            double? adjusted = AdjustBrightness(current, "brighter", max);
            if (adjusted.HasValue)
            {
                if (debug)
                    Console.WriteLine($"Increasing brightness from {current} to {adjusted.Value}");
                SetBrightness(adjusted.Value, debug);
            }
            else
            {
                if (debug)
                    Console.WriteLine($"Cannot increase brightness. Current: {current}");
            }
        }

        public static void DecreaseBrightness(bool debug)
        {
            var (current, rangeMode) = ReadSettings();
            double max = (rangeMode == "extended" ? 12.0 : 6.0);
            double? adjusted = AdjustBrightness(current, "darker", max);
            if (adjusted.HasValue)
            {
                if (debug)
                    Console.WriteLine($"Decreasing brightness from {current} to {adjusted.Value}");
                SetBrightness(adjusted.Value, debug);
            }
            else
            {
                if (debug)
                    Console.WriteLine($"Cannot decrease brightness. Current: {current}");
            }
        }

        public static void SetRangeMode(string mode, bool debug)
        {
            if (mode != "normal" && mode != "extended")
                return;
            ActiveRangeMode = mode;
            var (current, _) = ReadSettings();
            double max = (mode == "extended" ? 12.0 : 6.0);
            if (current > max)
                current = max;
            SetBrightness(current, debug);
            SaveSettings(current, mode);
            if (debug) Console.WriteLine($"Range mode set to {mode}. Current brightness: {current}");
        }
    }

    // ====================================
    // 4) TRAY APPLICATION CONTEXT
    // ====================================
    public class TrayApplicationContext : ApplicationContext
    {
        private NotifyIcon trayIcon;
        private bool debugMode;
        private InvisibleForm hiddenForm;
        private DateTime? lastIncreasePress = null;
        private DateTime? lastDecreasePress = null;
        private const int resetThresholdMs = 500;

        private ToolStripMenuItem currentBrightnessItem;
        private ToolStripMenuItem normalRangeItem;
        private ToolStripMenuItem extendedRangeItem;
        private ToolStripMenuItem startupItem;

        public TrayApplicationContext(bool debug)
        {
            debugMode = debug;
            hiddenForm = new InvisibleForm();
            hiddenForm.HotKeyPressed += HiddenForm_HotKeyPressed;

            // Register global hotkeys: Win+PgUp (id=1) and Win+PgDn (id=2)
            const uint MOD_WIN = 0x0008;
            const int HOTKEY_ID_INCREASE = 1;
            const int HOTKEY_ID_DECREASE = 2;
            const int VK_PRIOR = 0x21;
            const int VK_NEXT = 0x22;
            bool reg1 = hiddenForm.RegisterHotKey(HOTKEY_ID_INCREASE, MOD_WIN, VK_PRIOR);
            bool reg2 = hiddenForm.RegisterHotKey(HOTKEY_ID_DECREASE, MOD_WIN, VK_NEXT);
            if (debugMode)
                Console.WriteLine($"Hotkeys registration: Brighter={reg1}, Darker={reg2}");

            // Load settings from registry
            var settings = BrightnessController.ReadSettings();
            BrightnessController.ActiveRangeMode = settings.rangeMode;
            // At startup, set brightness on all monitors from registry
            BrightnessController.SetBrightness(settings.brightness, debugMode);

            currentBrightnessItem = new ToolStripMenuItem("Current brightness: " + settings.brightness.ToString("0.0"))
            {
                Enabled = false
            };

            var brighterItem = new ToolStripMenuItem("Brighter", null, (s, e) => { BrightnessController.IncreaseBrightness(debugMode); UpdateMenuText(); });
            var darkerItem = new ToolStripMenuItem("Darker", null, (s, e) => { BrightnessController.DecreaseBrightness(debugMode); UpdateMenuText(); });

            normalRangeItem = new ToolStripMenuItem("Normal range", null, (s, e) => { BrightnessController.SetRangeMode("normal", debugMode); UpdateMenuText(); });
            extendedRangeItem = new ToolStripMenuItem("Extended range", null, (s, e) => { BrightnessController.SetRangeMode("extended", debugMode); UpdateMenuText(); });

            startupItem = new ToolStripMenuItem("");
            startupItem.Click += (s, e) => ToggleStartup();

            var resetItem = new ToolStripMenuItem("restart app", null, (s, e) => RestartApp());
            var helpItem = new ToolStripMenuItem("Help", null, (s, e) => ShowHelp());
            var exitItem = new ToolStripMenuItem("Exit", null, (s, e) => ExitThread());

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add(currentBrightnessItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(brighterItem);
            contextMenu.Items.Add(darkerItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(normalRangeItem);
            contextMenu.Items.Add(extendedRangeItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(startupItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(resetItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(helpItem);
            contextMenu.Items.Add(exitItem);

            bool startup = IsStartupEnabled();
            startupItem.Text = "Start with Windows" + (startup ? " [active]" : "");

            // Load custom icon from compstui.dll with index 16 (adjust as needed)
            Icon trayIconImage = IconLoader.LoadDllIcon(@"C:\Windows\System32\compstui.dll", 16, debugMode);

            trayIcon = new NotifyIcon()
            {
                Icon = trayIconImage,
                ContextMenuStrip = contextMenu,
                Visible = true,
                Text = "Brightness Controller"
            };

            UpdateMenuText();
        }

        private bool IsStartupEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
            {
                if (key == null) return false;
                var val = key.GetValue("BrightnessTrayApp") as string;
                return !string.IsNullOrEmpty(val);
            }
        }

        private void ToggleStartup()
        {
            bool newState = !IsStartupEnabled();
            SetStartup(newState);
            Console.WriteLine(newState ? "Start with Windows activated." : "Start with Windows deactivated.");
            UpdateMenuText();
        }

        private void SetStartup(bool enabled)
        {
            string exePath = Application.ExecutablePath;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (enabled)
                    key.SetValue("BrightnessTrayApp", "\"" + exePath + "\"");
                else
                    key.DeleteValue("BrightnessTrayApp", false);
            }
        }

        private void UpdateMenuText()
        {
            var (brightness, range) = BrightnessController.ReadSettings();
            currentBrightnessItem.Text = "Current brightness: " + brightness.ToString("0.0");
            if (range == "normal")
            {
                normalRangeItem.Text = "[active] Normal range";
                extendedRangeItem.Text = "Extended range";
            }
            else
            {
                normalRangeItem.Text = "Normal range";
                extendedRangeItem.Text = "[active] Extended range";
            }
            bool startup = IsStartupEnabled();
            startupItem.Text = "Start with Windows" + (startup ? " [active]" : "");
        }

        private void HiddenForm_HotKeyPressed(object? sender, HotKeyEventArgs e)
        {
            if (e.HotKeyId == 1)
            {
                lastIncreasePress = DateTime.Now;
                if (lastDecreasePress.HasValue && (DateTime.Now - lastDecreasePress.Value).TotalMilliseconds < resetThresholdMs)
                {
                    Console.WriteLine("Both hotkeys detected – restarting application.");
                    RestartApp();
                    lastIncreasePress = lastDecreasePress = null;
                    return;
                }
                Console.WriteLine("Hotkey Win+PgUp detected – increasing brightness.");
                BrightnessController.IncreaseBrightness(debugMode);
                UpdateMenuText();
            }
            else if (e.HotKeyId == 2)
            {
                lastDecreasePress = DateTime.Now;
                if (lastIncreasePress.HasValue && (DateTime.Now - lastIncreasePress.Value).TotalMilliseconds < resetThresholdMs)
                {
                    Console.WriteLine("Both hotkeys detected – restarting application.");
                    RestartApp();
                    lastIncreasePress = lastDecreasePress = null;
                    return;
                }
                Console.WriteLine("Hotkey Win+PgDn detected – decreasing brightness.");
                BrightnessController.DecreaseBrightness(debugMode);
                UpdateMenuText();
            }
        }

        private void ShowHelp()
        {
			           MessageBox.Show(
                "Hotkeys:\n" +
                "  Win+PgUp – Increase brightness\n" +
                "  Win+PgDn – Decrease brightness\n" +
                "  Pressing both in quick succession restarts the application.\n\n" +
                "Tray Menu:\n" +
                "  Current brightness\n" +
                "  Brighter\n" +
                "  Darker\n" +
                "  Normal range / Extended range (active is marked [active])\n" +
                "  Start with Windows (toggle)\n" +
                "  Reset (restart app)\n" +
                "  Exit\n\n" +
                "Command-line parameters:\n" +
                "  /set {value}        - Set brightness (value within active range)\n" +
                "  /debug-set {value}  - Set brightness to any value (no boundaries)\n" +
                "  /brighter           - Increase brightness by 0.5 (respects boundaries)\n" +
                "  /ext-brighter       - Force extended mode and increase brightness by 0.5 (up to 12)\n" +
                "  /darker             - Decrease brightness by 0.5 (respects boundaries)\n\n" +
                "Run with /debug for additional messages.",
                "Help"
            );
            Console.WriteLine("Hotkeys:");
            Console.WriteLine("  Win+PgUp       – Increase brightness");
            Console.WriteLine("  Win+PgDn       – Decrease brightness");
            Console.WriteLine("  Pressing both in quick succession restarts the application.\n");
            Console.WriteLine("Command-line parameters:");
            Console.WriteLine("  /set {value}       - Set brightness (allowed range: 1-6)");
            Console.WriteLine("  /debug-set {value} - Set brightness to any value (no boundaries)");
            Console.WriteLine("  /brighter          - Increase brightness by 0.5 (up to 6, based on registry settings)");
            Console.WriteLine("  /ext-brighter      - Increase brightness by 0.5 (up to 12; does not change registry mode)");
            Console.WriteLine("  /darker            - Decrease brightness by 0.5 (down to 1)\n");
            Console.WriteLine("Tray Menu:");
            Console.WriteLine("  Current brightness");
            Console.WriteLine("  Brighter");
            Console.WriteLine("  Darker");
            Console.WriteLine("  Normal range / Extended range (active is marked [active])");
            Console.WriteLine("  Start with Windows (toggle)");
            Console.WriteLine("  restart app");
            Console.WriteLine("  Help");
            Console.WriteLine("  Exit");
        }

        private void RestartApp()
        {
            // Dispose tray icon and unregister hotkeys safely.
            try
            {
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                }
            }
            catch { }
            try
            {
                if (hiddenForm != null && !hiddenForm.IsDisposed)
                {
                    hiddenForm.UnregisterAllHotKeys();
                    hiddenForm.Close();
                }
            }
            catch { }
            Console.WriteLine("Restarting application...");
            Application.Restart();
            Environment.Exit(0);
        }

        protected override void ExitThreadCore()
        {
            try
            {
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                }
                if (hiddenForm != null && !hiddenForm.IsDisposed)
                {
                    hiddenForm.UnregisterAllHotKeys();
                    hiddenForm.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error during exit: " + ex.Message);
            }
            base.ExitThreadCore();
        }
    }

    // ====================================
    // 5) INVISIBLE FORM FOR HOTKEYS
    // ====================================
    public class InvisibleForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public event EventHandler<HotKeyEventArgs>? HotKeyPressed;

        public InvisibleForm()
        {
            this.ShowInTaskbar = false;
            this.FormBorderStyle = FormBorderStyle.None;
            this.WindowState = FormWindowState.Minimized;
            this.Load += (s, e) => { this.Visible = false; };
        }

        public bool RegisterHotKey(int id, uint modifiers, uint vk)
        {
            return RegisterHotKey(this.Handle, id, modifiers, vk);
        }

        public void UnregisterAllHotKeys()
        {
            UnregisterHotKey(this.Handle, 1);
            UnregisterHotKey(this.Handle, 2);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_HOTKEY = 0x0312;
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                HotKeyPressed?.Invoke(this, new HotKeyEventArgs(id));
            }
            base.WndProc(ref m);
        }
    }

    public class HotKeyEventArgs : EventArgs
    {
        public int HotKeyId { get; }
        public HotKeyEventArgs(int id)
        {
            HotKeyId = id;
        }
    }

    // ====================================
    // 6) MAIN with command-line parameters support
    // ====================================
    static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [STAThread]
        static void Main(string[] args)
        {
            // Jeśli są argumenty, uruchamiamy tryb command-line.
            if (args.Length > 0)
            {
                bool debugFlag = false;
                int index = 0;
                if (args[0].ToLower() == "/debug")
                {
                    debugFlag = true;
                    index++;
                    if (args.Length == index)
                    {
                        PrintCommandLineHelp();
                        return;
                    }
                }
                bool attached = AttachConsole(0xFFFFFFFF); // ATTACH_PARENT_PROCESS
                if (!attached)
                {
                    AllocConsole();
                }
                ProcessCommandLine(args, index, debugFlag);
                if (!attached)
                {
                    Console.WriteLine("Press any key to exit...");
                    Console.ReadKey();
                }
                return;
            }
            else
            {
                // No arguments: run tray mode.
                bool createdNew;
                using (var mutex = new System.Threading.Mutex(true, "Global\\BrightnessTrayApp", out createdNew))
                {
                    if (!createdNew)
                    {
                        // Already running: exit.
                        return;
                    }
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new TrayApplicationContext(false));
                }
            }
        }

        private static void ProcessCommandLine(string[] args, int startIndex, bool debug)
        {
            try
            {
                string command = args[startIndex].ToLower();
                bool success;
                double current;
                double? newValue;
                // Dla /set, dozwolony zakres to 1-6.
                double maxBoundary = 6.0;
                var settings = BrightnessController.ReadSettings();
                // Używamy aktualnego trybu z rejestru dla /brighter, /darker.
                string currentRange = settings.rangeMode;
                double currentMax = (currentRange == "extended" ? 12.0 : 6.0);
                switch (command)
                {
                    case "/set":
                        if (args.Length <= startIndex + 1)
                        {
                            Console.WriteLine("Usage: /set {value} (allowed range: 1-6)");
                            return;
                        }
                        if (!double.TryParse(args[startIndex + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double setValue))
                        {
                            Console.WriteLine("Invalid value.");
                            return;
                        }
                        if (setValue < BrightnessController.MIN_BRIGHTNESS) setValue = BrightnessController.MIN_BRIGHTNESS;
                        if (setValue > maxBoundary) setValue = maxBoundary;
                        success = BrightnessController.SetBrightness(setValue, debug);
                        Console.WriteLine($"Brightness set to {setValue}");
                        break;

                    case "/debug-set":
                        if (args.Length <= startIndex + 1)
                        {
                            Console.WriteLine("Usage: /debug-set {value}");
                            return;
                        }
                        if (!double.TryParse(args[startIndex + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double debugValue))
                        {
                            Console.WriteLine("Invalid value.");
                            return;
                        }
                        success = BrightnessController.SetBrightness(debugValue, true);
                        Console.WriteLine($"[DEBUG] Brightness set to {debugValue}");
                        break;

                    case "/brighter":
                        current = settings.brightness;
                        newValue = BrightnessController.AdjustBrightness(current, "brighter", currentMax);
                        if (newValue.HasValue)
                        {
                            success = BrightnessController.SetBrightness(newValue.Value, debug);
                            Console.WriteLine($"Brightness increased from {current} to {newValue.Value}");
                        }
                        else
                            Console.WriteLine($"Cannot increase brightness. Current: {current}");
                        break;

                    case "/ext-brighter":
                        current = settings.brightness;
                        newValue = BrightnessController.AdjustBrightness(current, "brighter", 12.0);
                        if (newValue.HasValue)
                        {
                            success = BrightnessController.SetBrightness(newValue.Value, debug);
                            Console.WriteLine($"[EXT] Brightness increased from {current} to {newValue.Value}");
                        }
                        else
                            Console.WriteLine($"[EXT] Cannot increase brightness. Current: {current}");
                        break;

                    case "/darker":
                        current = settings.brightness;
                        newValue = BrightnessController.AdjustBrightness(current, "darker", currentMax);
                        if (newValue.HasValue)
                        {
                            success = BrightnessController.SetBrightness(newValue.Value, debug);
                            Console.WriteLine($"Brightness decreased from {current} to {newValue.Value}");
                        }
                        else
                            Console.WriteLine($"Cannot decrease brightness. Current: {current}");
                        break;

                    case "/help":
                    case "/?":
                        PrintCommandLineHelp();
                        return;

                    default:
                        Console.WriteLine("Unknown command. Use /help for usage.");
                        return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
            }
        }

        private static void PrintCommandLineHelp()
        {
            Console.WriteLine("BrightnessTrayApp usage:");
            Console.WriteLine("  /set {value}       - Set brightness (allowed range: 1-6)");
            Console.WriteLine("  /debug-set {value} - Set brightness to any value (no boundaries)");
            Console.WriteLine("  /brighter          - Increase brightness by 0.5 (up to 6, based on registry settings)");
            Console.WriteLine("  /ext-brighter      - Increase brightness by 0.5 (up to 12; does not change registry mode)");
            Console.WriteLine("  /darker            - Decrease brightness by 0.5 (down to 1)");
            Console.WriteLine("  /help or /?        - Show this help message");
            Console.WriteLine("  /debug             - Enable debug mode (should be first parameter)");
        }
    }
}
