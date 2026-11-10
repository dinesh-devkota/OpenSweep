using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace OpenSweep.Core
{
    /// <summary>Swaps the Light/Dark palette dictionary at runtime. Mode is "System", "Light" or "Dark".</summary>
    public static class ThemeManager
    {
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWCP_ROUND = 2;

        public static bool IsDark { get; private set; }
        public static event Action Changed;
        static bool _hooked;

        public static void Apply(string mode)
        {
            bool dark = mode == "Dark" || (mode != "Light" && SystemPrefersDark());
            var dicts = Application.Current.Resources.MergedDictionaries;
            var palette = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/SqueakySift;component/Themes/{(dark ? "Dark" : "Light")}.xaml")
            };
            var old = dicts.FirstOrDefault(d => d.Source != null &&
                (d.Source.OriginalString.EndsWith("Light.xaml") || d.Source.OriginalString.EndsWith("Dark.xaml")));
            if (old != null) dicts[dicts.IndexOf(old)] = palette;
            else dicts.Insert(0, palette);

            IsDark = dark;
            foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);

            if (!_hooked)
            {
                _hooked = true;
                SystemEvents.UserPreferenceChanged += (s, e) =>
                {
                    if (e.Category == UserPreferenceCategory.General && AppSettings.Current.Theme == "System")
                        Application.Current?.Dispatcher.BeginInvoke(new Action(() => Apply("System")));
                };
            }
            Changed?.Invoke();
        }

        public static bool SystemPrefersDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        /// <summary>Uses native caption colors and Windows 11 window corners where supported.</summary>
        public static void ApplyTitleBar(Window w)
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                int on = IsDark ? 1 : 0;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));

                // Windows 11 rounds the native frame; Windows 10 ignores this unsupported attribute.
                // DWM still controls the square frame used when the window is maximized or snapped.
                int corners = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
            }
            catch { }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
