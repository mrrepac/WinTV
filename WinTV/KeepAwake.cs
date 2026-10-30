using System.Runtime.InteropServices;

namespace WinTV;

/// <summary>
/// Не даёт Windows гасить экран и уходить в сон, пока идёт воспроизведение.
/// Состояние привязано к вызывающему потоку — вызывать только из UI-потока.
/// </summary>
public static class KeepAwake
{
    const uint EsContinuous = 0x80000000;
    const uint EsSystemRequired = 0x00000001;
    const uint EsDisplayRequired = 0x00000002;

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);

    static bool _on;

    public static void Set(bool on)
    {
        if (on == _on) return;
        _on = on;
        SetThreadExecutionState(on ? EsContinuous | EsSystemRequired | EsDisplayRequired : EsContinuous);
    }
}
