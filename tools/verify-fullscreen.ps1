# verify-fullscreen.ps1 —— 验证「全屏游戏时自动隐藏」是否真的生效
#
# 不需要真的开游戏：脚本自己造一个**精确铺满显示器**的无边框窗口来冒充全屏应用。
#
# 为什么要造窗口，而不是"读一下配置"或者"看代码里那个函数返回什么"：
#   这个功能的成败全在**判定准不准**，而判定只能拿真窗口验。
#   本项目的老规矩 —— 别用代理指标代替结果验证。
#
# 用法（桌宠要已经在跑）：
#   powershell -ExecutionPolicy Bypass -File D:\ClaudePet\tools\verify-fullscreen.ps1
#
# 注意：中间几秒会有一个铺满屏幕的黑窗口，那是**故意的**。

param(
    [int]$SettleMs = 2200,   # 等桌宠的 500ms 轮询 + 2 次防抖跑完
    [switch]$NoNegative      # 跳过"最大化窗口不该隐藏"那一项
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class Probe {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, int flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

    // 进程必须在**建窗口之前**声明自己是 DPI 感知的，否则拿到的是被系统缩放过的
    // 虚拟坐标（本机 125% 下是 1536x864），跟桌宠看到的不一致，测试会假失败。
    public static void MakeDpiAware() { try { SetProcessDPIAware(); } catch {} }

    public static MONITORINFO PrimaryMonitor() {
        MONITORINFO mi = new MONITORINFO();
        mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        POINT p; p.X = 0; p.Y = 0;
        GetMonitorInfo(MonitorFromPoint(p, 2), ref mi);
        return mi;
    }

    // 返回该进程所有**顶层**窗口（不管可不可见）的 "class|visible|rect" 描述。
    // 用的是桌宠那套一模一样的读法，所以看到的数就是桌宠看到的数。
    public static List<string> WindowsOf(int wantPid) {
        List<string> list = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr p) {
            int pid; GetWindowThreadProcessId(h, out pid);
            if (pid != wantPid) return true;
            StringBuilder sb = new StringBuilder(256); GetClassName(h, sb, 256);
            RECT r; GetWindowRect(h, out r);
            list.Add(sb.ToString() + "|" + IsWindowVisible(h) + "|" + r.L + "," + r.T + "," + r.R + "," + r.B);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string RectOf(IntPtr h) {
        RECT r; GetWindowRect(h, out r);
        return r.L + "," + r.T + "," + r.R + "," + r.B;
    }

    public static string ForegroundClass() {
        IntPtr h = GetForegroundWindow();
        StringBuilder sb = new StringBuilder(256); GetClassName(h, sb, 256);
        return sb.ToString();
    }

    // 当前前台窗口是不是一个"真·全屏应用"。用来在开游戏时跳过测试：
    // 那种情况下我们造的测试窗口**抢不到前台**，量出来全是假 FAIL。
    public static bool ForegroundCoversMonitor() {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return false;
        RECT wr; if (!GetWindowRect(h, out wr)) return false;
        MONITORINFO mi = PrimaryMonitor();
        return wr.L == mi.rcMonitor.L && wr.T == mi.rcMonitor.T
            && wr.R == mi.rcMonitor.R && wr.B == mi.rcMonitor.B;
    }
}
'@

[Probe]::MakeDpiAware()

# ---- 找桌宠 ----
$pet = Get-Process Pet -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $pet) {
    Write-Output "FAIL: 没找到正在运行的 Pet.exe，请先启动桌宠再跑本脚本。"
    exit 2
}
$petPid = $pet.Id

# 配置里把功能关掉时，脚本的期望就整个反了 —— 不先提醒的话，会看到一堆看不懂的 FAIL。
$cfgPath = Join-Path (Split-Path (Split-Path $pet.Path -Parent) -Parent) 'config.json'
try {
    $cfg = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($cfg.PSObject.Properties.Name -contains 'fullscreenAutoHide' -and -not $cfg.fullscreenAutoHide) {
        Write-Output "SKIP: config.json 里 fullscreenAutoHide = false，功能是关的。"
        Write-Output "      这个状态下桌宠**本来就不该**躲，测不出东西来。"
        Write-Output "      要测功能本身：把配置改成 true（或右键菜单勾上）再跑。"
        exit 3
    }
} catch { }

# 已经全屏时（你真在打游戏），下面造的测试窗口抢不到前台，会量出一串假 FAIL。
# 顺带一提：这种状态下桌宠**本来就该是隐藏的**，所以 [1] 也会报一次"一开始就不可见"。
if ([Probe]::ForegroundCoversMonitor()) {
    Write-Output "SKIP: 当前前台窗口已经铺满整屏（你大概正在打游戏 / 看全屏视频）。"
    Write-Output "      测试要抢到前台才能量，现在抢不过它 —— 会量出一堆假 FAIL。"
    Write-Output "      退出全屏后再跑。"
    exit 4
}

# 桌宠的主窗口：WPF 顶层窗口的类名以 HwndWrapper 开头。
# 隐藏之后窗口依然能被 EnumWindows 枚举到，只是 IsWindowVisible 变 false —— 正是我们要看的。
function Get-PetWindowState {
    $all = [Probe]::WindowsOf($script:petPid)
    foreach ($w in $all) {
        $parts = $w.Split('|')
        if (-not $parts[0].StartsWith('HwndWrapper')) { continue }
        # 只认桌宠主窗口：**物理像素**下约 200 宽（160 逻辑 × 125% DPI）。
        # 不能图省事"取第一个 HwndWrapper" —— 菜单的点击接收层是整屏大的，而且它是
        # 复用设计（只 Hide 不销毁），只要打开过一次右键菜单它就一直在，
        # 而 EnumWindows 的顺序不保证。曾经因此把接收层当成桌宠，报出假的 FAIL。
        $rc = $parts[2].Split(',')
        $wpx = [int]$rc[2] - [int]$rc[0]
        if ($wpx -lt 100 -or $wpx -gt 400) { continue }
        return @{ Class = $parts[0]; Visible = ($parts[1] -eq 'True'); Rect = $parts[2]; Raw = $w }
    }
    return $null
}

$mon = [Probe]::PrimaryMonitor()
$mL = $mon.rcMonitor.L; $mT = $mon.rcMonitor.T; $mR = $mon.rcMonitor.R; $mB = $mon.rcMonitor.B
Write-Output "pet pid           : $petPid"
Write-Output "monitor (physical): $mL,$mT,$mR,$mB"

$fails = 0

# ---- 基线 ----
$st = Get-PetWindowState
if (-not $st) { Write-Output "FAIL: 枚举不到桌宠的顶层窗口。"; exit 2 }
Write-Output "[1] pet window    : class=$($st.Class) rect=$($st.Rect)"
Write-Output "[1] pet visible   : $($st.Visible)   (期望 True)"
if (-not $st.Visible) { $fails++; Write-Output "    ^ FAIL: 一开始就不可见" }

# ---- 造一个精确铺满显示器的窗口，冒充全屏游戏 ----
$fs = New-Object System.Windows.Forms.Form
$fs.FormBorderStyle = 'None'
$fs.StartPosition = 'Manual'
$fs.Bounds = New-Object System.Drawing.Rectangle($mL, $mT, ($mR - $mL), ($mB - $mT))
$fs.ShowInTaskbar = $false
$fs.BackColor = [System.Drawing.Color]::Black
$fs.Show()
$fs.Activate()
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Milliseconds 250
Write-Output "[2] fs window rect: $([Probe]::RectOf($fs.Handle))   (要和上面 monitor 完全一样)"
Write-Output "[2] foreground    : $([Probe]::ForegroundClass())   (要是 WindowsForms10...)"

Start-Sleep -Milliseconds $SettleMs
$st = Get-PetWindowState
Write-Output "[3] pet visible   : $($st.Visible)   (期望 False —— 应该躲起来)"
if ($st.Visible) { $fails++; Write-Output "    ^ FAIL: 全屏窗口在前台，桌宠却没躲" }

# ---- 关掉全屏窗口，应该自己回来 ----
$fs.Close()
$fs.Dispose()
Start-Sleep -Milliseconds $SettleMs
$st = Get-PetWindowState
Write-Output "[4] pet visible   : $($st.Visible)   (期望 True —— 应该自己回来)"
if (-not $st.Visible) { $fails++; Write-Output "    ^ FAIL: 退出全屏后没回来" }

# ---- 反向验证：最大化的普通窗口**不该**让它躲起来 ----
if (-not $NoNegative) {
    $mx = New-Object System.Windows.Forms.Form
    $mx.Text = 'ClaudePet verify - maximized (NOT fullscreen)'
    $mx.WindowState = 'Maximized'
    $mx.Show()
    $mx.Activate()
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 250
    Write-Output "[5] max window rect: $([Probe]::RectOf($mx.Handle))   (要**不等于** monitor)"
    Start-Sleep -Milliseconds $SettleMs
    $st = Get-PetWindowState
    Write-Output "[6] pet visible   : $($st.Visible)   (期望 True —— 最大化不算全屏，不该躲)"
    if (-not $st.Visible) { $fails++; Write-Output "    ^ FAIL: 把最大化窗口误判成全屏了" }
    $mx.Close()
    $mx.Dispose()
}

Start-Sleep -Milliseconds 400
if ($fails -eq 0) { Write-Output "`nRESULT: ALL PASS" } else { Write-Output "`nRESULT: $fails FAILED" }
exit $fails
