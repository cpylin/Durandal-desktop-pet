# install-autostart.ps1 —— 桌宠开机自启的开关
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File D:\ClaudePet\install-autostart.ps1           # 开启
#   powershell -ExecutionPolicy Bypass -File D:\ClaudePet\install-autostart.ps1 -Disable  # 关闭
#   powershell -ExecutionPolicy Bypass -File D:\ClaudePet\install-autostart.ps1 -Status   # 查看
#
# 为什么用注册表 Run 键，而不是把快捷方式丢进"启动"文件夹：
#   启动文件夹在 C 盘，而这个项目的原则是 **C 盘零占用**。
#   注册表项不新增文件 —— 而且任务管理器的「启动」标签页里一样能看到它、也能在那儿禁用。
#
# 只动 HKCU（当前用户），不需要管理员权限，也不会碰任何系统级设置。

param([switch]$Disable, [switch]$Status)

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe  = Join-Path $root "bin\Pet.exe"
$key  = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$name = "ClaudePet"

function Get-Current {
    return (Get-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue).$name
}

function Show-Status {
    $v = Get-Current
    if ($v) {
        Write-Host "当前状态：已开启（开机自动启动）"
        Write-Host ("  启动项   = " + $v)
    } else {
        Write-Host "当前状态：未开启"
    }
}

if ($Status) {
    Show-Status
    exit 0
}

if ($Disable) {
    $v = Get-Current
    if (-not $v) {
        Write-Host "本来就没开，无需操作。"
        exit 0
    }
    Remove-ItemProperty -Path $key -Name $name -ErrorAction Stop
    Write-Host "已关闭开机自启（删除了 HKCU 的 Run 项 " + $name + "）。"
    Write-Host "桌宠现在这份还在跑，重启之后就不会自己起来了。"
    exit 0
}

# 默认动作：开启
if (-not (Test-Path $exe)) {
    Write-Host ("找不到 " + $exe + "，先编译：cd /d/ClaudePet && ./build.sh") -ForegroundColor Red
    exit 1
}

Set-ItemProperty -Path $key -Name $name -Value ('"' + $exe + '"') -Type String
Write-Host "已开启开机自启。"
Write-Host ("  HKCU\Software\Microsoft\Windows\CurrentVersion\Run\" + $name + " = `"" + $exe + "`"")
Write-Host ""
Write-Host "取消：   本脚本加 -Disable"
Write-Host "看状态： 本脚本加 -Status"
Write-Host "另：任务管理器 → 启动 标签页里也能看到它、并在那里禁用。"
