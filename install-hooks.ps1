# install-hooks.ps1 —— 把桌宠的 hook 写入 Claude Code 的用户级配置
#
#   .\install-hooks.ps1              安装
#   .\install-hooks.ps1 -Uninstall   卸载
#   .\install-hooks.ps1 -DryRun      只打印会写入什么，不改文件
#
# 会先备份 settings.json。若配置里已存在 "hooks" 键则拒绝覆盖，
# 需要先手动合并（避免把你自己已有的 hook 冲掉）。

param(
    [switch]$Uninstall,
    [switch]$DryRun,
    [string]$PetExe = 'D:\ClaudePet\bin\PetNotify.exe'
)

$ErrorActionPreference = 'Stop'

$settingsPath = Join-Path $env:USERPROFILE '.claude\settings.json'

if (-not (Test-Path $settingsPath)) {
    throw "找不到配置文件：$settingsPath（请先运行一次 Claude Code）"
}

if (-not $Uninstall -and -not (Test-Path $PetExe)) {
    throw "找不到 $PetExe，请先在 Git Bash 里执行 ./build.sh 编译"
}

$raw = Get-Content $settingsPath -Raw -Encoding UTF8
if ([string]::IsNullOrWhiteSpace($raw)) { $raw = '{}' }
$settings = $raw | ConvertFrom-Json

# ---------- 卸载 ----------
if ($Uninstall) {
    if (-not $settings.PSObject.Properties.Match('hooks').Count) {
        Write-Host '配置里没有 hooks 键，无需卸载。' -ForegroundColor Yellow
        return
    }
    if ($DryRun) {
        Write-Host '[DryRun] 会删除 settings.json 中的 hooks 键' -ForegroundColor Cyan
        return
    }
    $backup = "$settingsPath.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
    Copy-Item $settingsPath $backup
    $settings.PSObject.Properties.Remove('hooks')
    ($settings | ConvertTo-Json -Depth 20) | Set-Content $settingsPath -Encoding UTF8
    Write-Host "已移除 hooks。备份：$backup" -ForegroundColor Green
    return
}

# ---------- 安装 ----------
if ($settings.PSObject.Properties.Match('hooks').Count) {
    throw "settings.json 里已经有 hooks 键了，为免覆盖你已有的配置，请手动合并 hooks\hooks.json 的内容。"
}

# command 只放 exe 路径，状态放进 args —— 这是 exec 形式：Claude Code 直接把
# exe spawn 起来，不经过任何 shell。
#
# 千万不要写成 "$PetExe $state" 拼成一整串：那种写法会交给 shell 解析，
# 而 Git Bash 里未加引号的反斜杠是转义符，D:\ClaudePet\bin\PetNotify.exe
# 会被吃成 D:ClaudePetbinPetNotify.exe，所有 hook 静默失效（只报 command not found）。
function Hook($state) {
    @{ type = 'command'; command = $PetExe; args = @($state); timeout = 5 }
}

$hooks = [ordered]@{
    SessionStart = @(
        @{ matcher = '*'; hooks = @( (Hook 'awake') ) }
    )
    UserPromptSubmit = @(
        @{ hooks = @( (Hook 'think') ) }
    )
    PreToolUse = @(
        @{ matcher = '*'; hooks = @( (Hook 'work') ) }
    )
    PostToolUse = @(
        @{ matcher = '*'; hooks = @( (Hook 'think') ) }
    )
    PostToolUseFailure = @(
        @{ matcher = '*'; hooks = @( (Hook 'error') ) }
    )
    Notification = @(
        @{ matcher = 'permission_prompt'; hooks = @( (Hook 'alert') ) }
        @{ matcher = 'idle_prompt';       hooks = @( (Hook 'sleep') ) }
    )
    Stop = @(
        @{ hooks = @( (Hook 'done') ) }
    )
    SessionEnd = @(
        @{ hooks = @( (Hook 'sleep') ) }
    )
}

$settings | Add-Member -NotePropertyName 'hooks' -NotePropertyValue $hooks -Force

$out = $settings | ConvertTo-Json -Depth 20

if ($DryRun) {
    Write-Host '[DryRun] 将写入以下内容（不会修改文件）：' -ForegroundColor Cyan
    Write-Host $out
    return
}

$backup = "$settingsPath.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
Copy-Item $settingsPath $backup
$out | Set-Content $settingsPath -Encoding UTF8

Write-Host "已安装桌宠 hook。" -ForegroundColor Green
Write-Host "  配置：$settingsPath"
Write-Host "  备份：$backup"
Write-Host ""
Write-Host "重启 Claude Code 后生效。要卸载请运行： .\install-hooks.ps1 -Uninstall"
