# verify-clicks.ps1 —— 验证 2026-09-12 改的三件交互（左键说话 / 右键切状态 / 隐藏到托盘）
#
# 为什么需要它：这三件都是**运行时行为**，光看代码验不了；而气泡和托盘又分别是
# 分层窗口内容和系统通知区域，截图截不到（README 坑 6）。所以：
#   * 读气泡内容 → UI Automation
#   * 读窗口可见性 → EnumWindows 自己数
#   * 点击 → 真鼠标注入（PostMessage 合成的 WPF 不认，见坑 18）
#
# 用法（桌宠要在跑、且当前可见）：
#   powershell -ExecutionPolicy Bypass -File D:\ClaudePet\tools\verify-clicks.ps1
#
# 副作用：会真移动鼠标、点桌宠、弹一次菜单、把它藏进托盘再叫回来。测完光标不还原
#        （还原那一下可能正好扫过桌宠，反而多一次点击）。
# exit 0 = 通过。

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class CK {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static void MakeDpiAware() { try { SetProcessDPIAware(); } catch {} }

    // 只认桌宠主窗口：按**尺寸**筛（物理像素下约 200 宽）。
    // 不能"取第一个可见的 HwndWrapper" —— 菜单的点击接收层整屏大、而且只 Hide 不销毁，
    // 一旦被认成桌宠，后面所有点击和断言都打在空气上（README 坑里记过这个假通过）。
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
            if (w < 100 || w > 400) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }
}
'@

[CK]::MakeDpiAware()

$pet = Get-Process Pet -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $pet) { Write-Output 'FAIL: 桌宠没在跑。'; exit 2 }

# ---- 工具函数 ----
function Get-PetWindow() { return [CK]::PetWindow($pet.Id) }

function Read-Bubble([IntPtr]$hwnd) {
    if ($hwnd -eq [IntPtr]::Zero) { return '' }
    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        if (-not $root) { return '' }
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text)
        $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($el) { return $el.Current.Name }
    } catch { }
    return ''
}

function Click-At([int]$x, [int]$y, [bool]$right) {
    [void][CK]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 120
    $down = if ($right) { 0x0008 } else { 0x0002 }
    $up   = if ($right) { 0x0010 } else { 0x0004 }
    [CK]::mouse_event($down, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 50
    [CK]::mouse_event($up, 0, 0, 0, [IntPtr]::Zero)
}

$fails = New-Object System.Collections.ArrayList
function Check([string]$name, [bool]$ok, [string]$detail) {
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Output ("[$tag] " + $name + "  " + $detail)
    if (-not $ok) { [void]$fails.Add($name) }
}

# 状态名（右键切状态后气泡里应该出现这些；台词里**不该**出现这些）
$stateLabels = @('待机','在想事情','正在干活','等你确认','干完了','回来了','睡着了','出错了','已隐藏')
# 台词（左键说话后应该出现其中之一）。只列几个代表性的，够区分就行。
$someLines = @('站直了。','待机也是修行。','别因为周末就松懈。','要不要做几组挥枪练习？',
               '让我想想。','一条一条来。','结果才是这个世界的语言。','取舍，总是要做的。',
               '开始行动。','体力活交给我。','专心的时候别催我。','认真起来就没那么快。',
               '这里需要你点个头。','我不过是在贯彻我的正义。','在等你，快一点。','决定权在你。',
               '非常、非常了不起。','完成了。下一个。','收工。','这就是所谓的成长吧。',
               '回来了，开始吧。','别来无恙？','是幽兰黛尔，也是卡斯兰娜。','站在巨人的肩上。',
               '……zzz……','就一会儿……别吵……','（抱紧了枕头）','消灭崩坏之后再想生活。',
               '还不到我放弃的时候。','这次我来盯。','是我的责任。','重新来过。')

$h = Get-PetWindow
if ($h -eq [IntPtr]::Zero) { Write-Output 'FAIL: 找不到可见的桌宠窗口（可能正被全屏自动隐藏藏着）。'; exit 2 }

$r = New-Object CK+RECT
[void][CK]::GetWindowRect($h, [ref]$r)
$cx = [int](($r.L + $r.R) / 2)
$cy = [int](($r.T + $r.B) / 2)
Write-Output ("桌宠窗口 rect=" + $r.L + "," + $r.T + "," + $r.R + "," + $r.B + "  点(" + $cx + "," + $cy + ")")
Write-Output ''

# ---- [1] 左键单击 = 说话，状态不变 ----
$before = Read-Bubble $h
Click-At $cx $cy $false
Start-Sleep -Milliseconds 400
$after1 = Read-Bubble $h
$isLine = $someLines -contains $after1
$isState = $stateLabels -contains $after1
Check '[1] 左键单击说的是台词' ($isLine -and -not $isState) ("气泡=" + $after1 + "  （之前=" + $before + "）")

# ---- [2] 右键单击 = 切状态（气泡里出现状态名，且和左键那句不同）----
Click-At $cx $cy $true
Start-Sleep -Milliseconds 900      # 必须 > RightDblClickMs(350)，否则会被当成双击
$after2 = Read-Bubble $h
$isState2 = $stateLabels -contains $after2
Check '[2] 右键单击切了状态' $isState2 ("气泡=" + $after2)

# ---- [3] 隐藏到托盘 ----
# 右键双击开菜单（两下间隔必须 < 350ms）
Click-At $cx $cy $true
Start-Sleep -Milliseconds 80
[CK]::mouse_event(0x0008, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 50
[CK]::mouse_event(0x0010, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 900

$hideItem = $null
for ($t = 0; $t -lt 10; $t++) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, '隐藏到托盘')
    $hideItem = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($hideItem) { break }
    Start-Sleep -Milliseconds 300
}
if (-not $hideItem) {
    Check '[3] 隐藏到托盘' $false '菜单里找不到「隐藏到托盘」（菜单没弹出来？）'
} else {
    $hideItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
    $h2 = Get-PetWindow
    Check '[3] 隐藏到托盘后窗口消失' ($h2 -eq [IntPtr]::Zero) ("可见桌宠窗口=" + $(if ($h2 -eq [IntPtr]::Zero) { '无' } else { '还在' }))
}

# ---- [4] 隐藏期间来了 Claude 事件，**不能**自己冒出来（"真待命"的核心承诺）----
# 直接往它的 UDP 端口发一个真事件（和 PetNotify.exe 发的格式一样：状态|工具|会话id|备注）
$cfg = Get-Content (Join-Path $PSScriptRoot '..\config.json') -Raw | ConvertFrom-Json
$udpPort = if ($cfg.port) { [int]$cfg.port } else { 47821 }
$udp = New-Object System.Net.Sockets.UdpClient
$b = [System.Text.Encoding]::UTF8.GetBytes('work|Bash||')
[void]$udp.Send($b, $b.Length, '127.0.0.1', $udpPort)
$udp.Close()
Start-Sleep -Milliseconds 1800         # 比两个 tick(500ms) 还长，足够它处理完
$hStill = Get-PetWindow
Check '[4] 隐藏期间来事件也不冒出来' ($hStill -eq [IntPtr]::Zero) ("发了个 work 事件后，可见桌宠窗口=" + $(if ($hStill -eq [IntPtr]::Zero) { '无' } else { '有' }))

# ---- [5] 双击 exe 召唤：从托盘回来 ----
# 再启动一次 Pet.exe：单实例逻辑会给在跑的那只发 locate（等于"双击 exe"）
Start-Process -FilePath (Join-Path $PSScriptRoot '..\bin\Pet.exe') | Out-Null
Start-Sleep -Milliseconds 2500
$h3 = Get-PetWindow
Check '[5] 召唤后从托盘回来' ($h3 -ne [IntPtr]::Zero) ("可见桌宠窗口=" + $(if ($h3 -eq [IntPtr]::Zero) { '无' } else { '有' }))

Write-Output ''
if ($fails.Count -eq 0) { Write-Output 'RESULT: ALL PASS'; exit 0 }
Write-Output ('RESULT: FAIL -> ' + ($fails -join ', '))
exit 1
