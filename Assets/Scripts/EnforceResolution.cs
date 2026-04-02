// Assets/Scripts/Win/EnforceWindowResolution.cs
// Windows-only. Add to any GameObject. Set Width/Height and click Apply in Play Mode.
// If you need it at boot, enable ApplyOnStart.

#if UNITY_STANDALONE_WIN

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

public class EnforceResolution : MonoBehaviour
{
    [Header("Target client size (Unity render area)")]
    public int Width = 1920;

    public int Height = 1080;

    [Header("Behavior")]
    public bool ApplyOnStart = true;      // apply once on Start

    public bool ReapplyOnFocus = true;    // re-apply when focus returns (useful if OS or user resizes)
    public bool ForceWindowed = true;     // ensure windowed style before sizing

    private void Start()
    {
        if (ApplyOnStart) Apply();
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus && ReapplyOnFocus) Apply();
    }

    [ContextMenu("Apply Now")]
    public void Apply()
    {
#if UNITY_EDITOR
        return;
#endif

        IntPtr hWnd = GetUnityHwnd();
        if (hWnd == IntPtr.Zero)
        {
            UnityEngine.Debug.LogWarning("Main window handle not found.");
            return;
        }

        // Optional: force windowed style so sizing behaves predictably
        if (ForceWindowed)
        {
            ForceStandardWindowStyle(hWnd);
        }

        // Get current window placement to preserve top-left
        RECT winRect;
        if (!GetWindowRect(hWnd, out winRect))
        {
            UnityEngine.Debug.LogWarning("GetWindowRect failed.");
            return;
        }
        int left = winRect.left;
        int top = winRect.top;

        // Compute required OUTER window size for desired CLIENT size
        int style = GetWindowLong(hWnd, GWL_STYLE);
        int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
        int dpi = GetDpiForWindowSafe(hWnd);

        RECT target = new RECT { left = 0, top = 0, right = Width, bottom = Height };
        bool ok = AdjustWindowRectForDpiSafe(ref target, style, false, ex, dpi);
        if (!ok)
        {
            UnityEngine.Debug.LogWarning("AdjustWindowRectEx/ForDpi failed.");
            return;
        }

        int outW = target.right - target.left;
        int outH = target.bottom - target.top;

        // Apply size and keep current top-left
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;
        SetWindowPos(hWnd, IntPtr.Zero, left, top, outW, outH, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // ——— helpers ———

    private static IntPtr GetUnityHwnd()
    {
        // Reliable in most cases
        IntPtr hWnd = Process.GetCurrentProcess().MainWindowHandle;
        if (hWnd != IntPtr.Zero) return hWnd;

        // Fallback by class name (Unity uses "UnityWndClass")
        hWnd = FindWindow("UnityWndClass", null);
        return hWnd;
    }

    private static void ForceStandardWindowStyle(IntPtr hWnd)
    {
        const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
        const int WS_VISIBLE = 0x10000000;

        int style = GetWindowLong(hWnd, GWL_STYLE);
        style &= ~WS_POPUP;                       // clear popup-style if present
        style |= WS_OVERLAPPEDWINDOW | WS_VISIBLE;
        SetWindowLong(hWnd, GWL_STYLE, style);

        // Clear topmost etc. if present
        int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
        SetWindowLong(hWnd, GWL_EXSTYLE, ex);

        ShowWindow(hWnd, SW_SHOWNOACTIVATE);
    }

    private static bool AdjustWindowRectForDpiSafe(ref RECT r, int style, bool hasMenu, int exStyle, int dpi)
    {
        // Try Win10+ DPI-aware API
        try
        {
            if (AdjustWindowRectExForDpi(ref r, style, hasMenu, exStyle, (uint)dpi))
                return true;
        }
        catch { /* API may not exist on older Windows */ }

        // Fallback to classic API
        return AdjustWindowRectEx(ref r, style, hasMenu, exStyle);
    }

    private static int GetDpiForWindowSafe(IntPtr hWnd)
    {
        try { return (int)GetDpiForWindow(hWnd); }
        catch { /* not available */ }

        IntPtr hdc = GetDC(IntPtr.Zero);
        if (hdc != IntPtr.Zero)
        {
            int dpi = GetDeviceCaps(hdc, LOGPIXELSX);
            ReleaseDC(IntPtr.Zero, hdc);
            if (dpi > 0) return dpi;
        }
        return 96; // default
    }

    // ——— Win32 ———
    private const int GWL_STYLE = -16;

    private const int GWL_EXSTYLE = -20;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int LOGPIXELSX = 88;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    { public int left, top, right, bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AdjustWindowRectEx(ref RECT lpRect, int dwStyle, bool bMenu, int dwExStyle);

    // Available on Windows 10+; may throw on older systems when invoked
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "AdjustWindowRectExForDpi")]
    private static extern bool AdjustWindowRectExForDpi(ref RECT lpRect, int dwStyle, bool bMenu, int dwExStyle, uint dpi);

    // Win10+; may throw on older systems when invoked
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    // Style bit (used in ForceStandardWindowStyle)
    private const int WS_POPUP = unchecked((int)0x80000000);
}

#endif