param(
    [string]$DataDirectory = "",
    [switch]$Apply
)

$ErrorActionPreference = "Stop"

function Get-DirectorySizeText {
    param([string]$Path)
    try {
        $files = Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue
        $bytes = ($files | Measure-Object -Property Length -Sum).Sum
        if ($null -eq $bytes) { $bytes = 0 }
        $text = if ($bytes -ge 1GB) { "{0:0.##} GB" -f ($bytes / 1GB) }
                elseif ($bytes -ge 1MB) { "{0:0.#} MB" -f ($bytes / 1MB) }
                else { "{0:0.#} KB" -f ($bytes / 1KB) }
        return "$text / $($files.Count) 个文件"
    }
    catch { return "无法读取" }
}

$candidates = @()
if ($DataDirectory) { $candidates += $DataDirectory }
$candidates += (Join-Path $PSScriptRoot "data")
$candidates += (Join-Path (Get-Location) "data")

$resolvedData = $null
foreach ($candidate in $candidates | Select-Object -Unique) {
    if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
    $full = [System.IO.Path]::GetFullPath($candidate)
    if (Test-Path -LiteralPath (Join-Path $full "settings.json")) {
        $resolvedData = $full
        break
    }
}

if ($null -eq $resolvedData) {
    Write-Host "找不到新数据目录（需要其中的 settings.json）。" -ForegroundColor Yellow
    Write-Host "请先运行新版 exe 一次，让它建立 data 目录；或传入 -DataDirectory。" -ForegroundColor Yellow
    exit 2
}

Write-Host "新数据目录：$resolvedData"
$legacyRoaming = Join-Path $env:APPDATA "PackDependencyTool"
$legacyLocal = Join-Path $env:LOCALAPPDATA "PackDependencyTool"

$pending = @()
foreach ($legacy in @($legacyRoaming, $legacyLocal)) {
    if (Test-Path -LiteralPath $legacy) {
        $pending += [pscustomobject]@{
            Path = $legacy
            Size = (Get-DirectorySizeText $legacy)
        }
    }
}

if ($pending.Count -eq 0) {
    Write-Host "C 盘旧目录已经不存在，无需清理。" -ForegroundColor Green
    exit 0
}

$running = Get-Process -Name "PackDependencyTool" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "PackDependencyTool 正在运行，请先完全退出后再清理。" -ForegroundColor Red
    exit 3
}

Write-Host ""
Write-Host "将清理以下旧目录：" -ForegroundColor Cyan
foreach ($item in $pending) {
    Write-Host ("  {0}  ({1})" -f $item.Path, $item.Size)
}

if (-not $Apply) {
    Write-Host ""
    Write-Host "当前是预览模式，没有删除任何文件。" -ForegroundColor Yellow
    Write-Host "确认新目录正常后，重新运行并加 -Apply：" -ForegroundColor Yellow
    Write-Host "  powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Apply"
    exit 0
}

$backupRoot = Join-Path $resolvedData ("_legacy-backup-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
Write-Host ""
Write-Host "旧目录会先移动到：$backupRoot" -ForegroundColor Cyan

$index = 0
foreach ($item in $pending) {
    $index++
    $source = $item.Path
    $name = if ([System.IO.Path]::GetFullPath($source) -eq [System.IO.Path]::GetFullPath($legacyRoaming)) { "roaming" } else { "local" }
    $destination = Join-Path $backupRoot ("{0}-{1}" -f $index, $name)
    try {
        Move-Item -LiteralPath $source -Destination $destination -Force
        Write-Host "  已移走：$source" -ForegroundColor Green
    }
    catch {
        Write-Host "  移动失败：$source" -ForegroundColor Red
        Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "完成。C 盘旧目录已移走；备份在新数据目录的 _legacy-backup-* 中。" -ForegroundColor Green
Write-Host "确认无误后可手动删除该备份。" -ForegroundColor Green
