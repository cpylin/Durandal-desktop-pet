# verify-nofocus.ps1 —— 验证「点桌宠不抢焦点」（README 坑 18 那条）
#
# 重点场景：从全屏游戏**回来之后**。新增的「全屏自动隐藏」会 Hide 再 Show 主窗口，
# 而 Show() 会重新应用窗口样式、把 WS_EX_NOACTIVATE 冲掉 —— 这个坑在菜单接收层上
# 已经踩过一次。这个脚本就是对主窗口做一次同样的回归。
#
# 用**真·鼠标注入**（SetCursorPos + mouse_event），不要用 PostMessage：
# WPF 不认合成窗口消息，它按真实鼠标状态判断（本项目踩过，README 有记）。
#
# 用法（桌宠要正在运行、且当前可见）：
#   powershell -ExecutionPolicy Bypass -File D:\ClaudePet\tools\verify-nofocus.ps1
#
# 副作用：会把鼠标移到桌宠身上点一下（桌宠会因此换一个状态，这是它的正常交互），
#         测完把光标放回原处。

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class NF {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    public static void MakeDpiAware() { try { SetProcessDPIAware(); } catch {} }

    public static IntPtr PetWindow(int wantPid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr p) {
            int pid; GetWindowThreadProcessId(h, out pid);
            if (pid != wantPid) return true;
            if (!IsWindowVisible(h)) return true;
            StringBuilder sb = new StringBuilder(256); GetClassName(h, sb, 256);
            if (!sb.ToString().StartsWith("HwndWrapper")) return true;
            // 只认桌宠主窗口（物理像素下约 200 宽）。必须按尺寸筛，不能"取第一个可见的" ——
            // 菜单打开期间点击接收层是可见的且整屏大，会被认成桌宠，点它当然不抢焦点，
            // 于是这个测试会**假装通过**。这里加了尺寸限制才真正测的是桌宠本体。
            RECT r; GetWindowRect(h, out r);
            int w = r.R - r.L;
            if (w < 100 || w > 400) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }

    public static string Describe(IntPtr h) {
        StringBuilder sb = new StringBuilder(256); GetWindowText(h, sb, 256);
        string title = sb.ToString();
        StringBuilder c = new StringBuilder(256); GetClassName(h, c, 256);
        return "class=" + c.ToString() + " title='" + title + "'";
    }
}
'@

[NF]::MakeDpiAware()

$pet = Get-Process Pet -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $pet) { Write-Output "FAIL: 桌宠没在跑。"; exit 2 }

$h = [NF]::PetWindow($pet.Id)
if ($h -eq [IntPtr]::Zero) {
    Write-Output "FAIL: 找不到可见的桌宠窗口（它可能正被全屏自动隐藏藏着）。"
    exit 2
}

$r = New-Object NF+RECT
[void][NF]::GetWindowRect($h, [ref]$r)
$cx = [int](($r.L + $r.R) / 2)
$cy = [int](($r.T + $r.B) / 2)
$ex = [NF]::GetWindowLong($h, -20)

Write-Output ("pet hwnd      : 0x{0:X}" -f [int64]$h)
Write-Output ("pet rect      : {0},{1},{2},{3}   -> click at {4},{5}" -f $r.L, $r.T, $r.R, $r.B, $cx, $cy)
Write-Output ("exStyle       : 0x{0:X8}   (WS_EX_NOACTIVATE=0x08000000 bit: {1})" -f $ex, [bool]($ex -band 0x08000000))
Write-Output ""

$before = [NF]::GetForegroundWindow()
Write-Output ("foreground BEFORE click : " + [NF]::Describe($before))

$orig = New-Object NF+POINT
[void][NF]::GetCursorPos([ref]$orig)

# 真注入：移动 + 按下 + 抬起（抬起之后再动光标才不会把窗口拖走）
[void][NF]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 120
[NF]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)   # LEFTDOWN
Start-Sleep -Milliseconds 60
[NF]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)   # LEFTUP
Start-Sleep -Milliseconds 350

$after = [NF]::GetForegroundWindow()
Write-Output ("foreground AFTER  click : " + [NF]::Describe($after))

# 光标放回原处。按键已经抬起，这个动作不会拖动窗口。
[void][NF]::SetCursorPos($orig.X, $orig.Y)

Write-Output ""
if ($before -eq $after) {
    Write-Output "RESULT: PASS —— 点了桌宠，前台窗口没变，焦点没被抢。"
    exit 0
} else {
    Write-Output "RESULT: FAIL —— 焦点被抢走了（前台窗口变了）。"
    exit 1
}
