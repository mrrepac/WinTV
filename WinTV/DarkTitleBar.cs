using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinTV;

/// <summary>Тёмный заголовок окна Windows 10/11.</summary>
public static class DarkTitleBar
{
    const int DwmwaUseImmersiveDarkModeOld = 19;
    const int DwmwaUseImmersiveDarkMode = 20;
    const int DwmwaCaptionColor = 35;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Window w)
    {
        void Set()
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            int on = 1;
            if (DwmSetWindowAttribute(h, DwmwaUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(h, DwmwaUseImmersiveDarkModeOld, ref on, sizeof(int));
            // Цвет заголовка как у фона приложения (#0F1115, COLORREF = 0x00BBGGRR), работает на Windows 11.
            int color = 0x0015110F;
            DwmSetWindowAttribute(h, DwmwaCaptionColor, ref color, sizeof(int));
        }

        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Set();
        else w.SourceInitialized += (_, _) => Set();
    }
}
