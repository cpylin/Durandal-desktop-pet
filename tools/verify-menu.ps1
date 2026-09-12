# verify-menu.ps1 —— 把右键菜单**真的打开**，然后把里面的菜单项读出来核对
#
# 为什么需要它：菜单是新项目里最难验证的一块（分层窗口截不到图），而"能打开"和
# "里面的项对、顺序对"是两回事。改动 BuildMenu() 之后，光看代码没用 ——
# WPF 菜单项是运行时才建出来的。
#
# 做法：真鼠标注入右键（不能用 PostMessage，WPF 不认合成窗口消息，见 README 坑 18），
# 再用 UI Automation 读那个 popup 里的文字。
#
# 用法（桌宠要在跑）：
#   powershell -ExecutionPolicy Bypass -File D:\ClaudePet\tools\verify-menu.ps1
#
# 副作用：会在桌宠身上右键一下（菜单会闪开又关掉），并短暂移动鼠标。

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class M {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
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
            RECT r; GetWindowRect(h, out r);
            int w = r.R - r.L;
            // 桌宠主窗口在**物理像素**下约 200 宽（160 逻辑 × 125% DPI）——
            // 别拿逻辑值 160 来筛，GetWindowRect 给的是物理像素，那样一个都匹配不上。
            // 这个区间同时把"点击接收层"（整屏大）和提示卡片排除掉。
            if (w < 100 || w > 400) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }
}
'@

[M]::MakeDpiAware()

$pet = Get-Process Pet -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $pet) { Write-Output 'FAIL: 桌宠没在跑。'; exit 2 }

$h = [M]::PetWindow($pet.Id)
if ($h -eq [IntPtr]::Zero) { Write-Output 'FAIL: 找不到桌宠主窗口（可能正被全屏自动隐藏藏着）。'; exit 2 }

$r = New-Object M+RECT
[void][M]::GetWindowRect($h, [ref]$r)
$cx = [int](($r.L + $r.R) / 2)
$cy = [int](($r.T + $r.B) / 2)

$orig = New-Object M+POINT
[void][M]::GetCursorPos([ref]$orig)

# 真鼠标右键：移动 + 按下 + 抬起
[void][M]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 150
[M]::mouse_event(0x0008, 0, 0, 0, [IntPtr]::Zero)   # RIGHTDOWN
Start-Sleep -Milliseconds 60
[M]::mouse_event(0x0010, 0, 0, 0, [IntPtr]::Zero)   # RIGHTUP
Start-Sleep -Milliseconds 700

# 把桌宠进程里所有窗口的可读文字都蒐出来（菜单 popup 属于同一个进程）
$root = [System.Windows.Automation.AutomationElement]::RootElement

# 子菜单默认是**折叠**的，折叠时里面的项还没被实例化，UIA 一个都读不到。
# 必须显式展开 —— 靠"鼠标正好停在它上面"碰运气是不行的：同一个脚本实测有时能读到
# 8 个状态、有时一个都读不到，那种"有时通过"的测试比没有更糟。
$subCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, '测试各状态')
$sub = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $subCond)
if ($sub) {
    try {
        $ec = $sub.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $ec.Expand()
        Start-Sleep -Milliseconds 500
    } catch {
        Write-Output ('WARN: 展开「测试各状态」失败: ' + $_.Exception.Message)
    }
} else {
    Write-Output 'WARN: 没找到「测试各状态」这一项'
}

$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $pet.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
$names = New-Object System.Collections.ArrayList
foreach ($w in $wins) {
    $kids = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($k in $kids) {
        $t = $k.Current.Name
        if ($t -and $t.Trim().Length -gt 0 -and -not $names.Contains($t)) { [void]$names.Add($t) }
    }
}

# 关掉菜单：再右键一次是切换；再用 Esc 兜底
[M]::mouse_event(0x0008, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 60
[M]::mouse_event(0x0010, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 300
[void][M]::SetCursorPos($orig.X, $orig.Y)   # 按键已抬起，不会拖走窗口

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('读到的菜单项：')
foreach ($n in $names) { [void]$sb.AppendLine('  ' + $n) }

# 期望的 8 个状态（顺序 = CycleOrder）
$want = @('idle (待机)','think (思考)','work (工作)','alert (等你确认)','done (完成)','awake (回来了)','sleep (睡觉)','error (出错)')
$missing = @()
foreach ($w in $want) { if (-not $names.Contains($w)) { $missing += $w } }
[void]$sb.AppendLine('')
[void]$sb.AppendLine('缺失的状态项：' + $(if ($missing.Count) { $missing -join ', ' } else { '（无）' }))
[void]$sb.AppendLine('顶层菜单项齐不齐：' + [bool](($names -contains '大小') -and ($names -contains '透明度') -and ($names -contains '回到右下角') -and ($names -contains '重新载入素材') -and ($names -contains '测试各状态') -and ($names -contains '开机自启') -and ($names -contains '全屏游戏时自动隐藏') -and ($names -contains '退出')))
[void]$sb.AppendLine('RESULT: ' + $(if ($missing.Count -eq 0) { 'PASS' } else { 'FAIL' }))

[System.IO.File]::WriteAllText($env:TEMP + '\petmenu.txt', $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Output ('written to ' + $env:TEMP + '\petmenu.txt')
exit $(if ($missing.Count -eq 0) { 0 } else { 1 })
