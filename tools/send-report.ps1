# send-report.ps1 — собрать всё, что мод записал о лагах, в ОДИН файл
# и положить его на рабочий стол. Дальше перетащите этот файл в чат
# с ассистентом Arena — по нему правится следующая версия мода.
#
# Пример:
#   powershell -ExecutionPolicy Bypass -File send-report.ps1
#   powershell -ExecutionPolicy Bypass -File send-report.ps1 -Minutes 60

param(
    [string]$SavePath = "$env:USERPROFILE\Documents\My Games\Terraria\tModLoader\EngineOptimizer",
    [int]$Minutes = 0,
    [string]$OutPath = "$env:USERPROFILE\Desktop"
)

$ErrorActionPreference = 'Continue'

if (-not (Test-Path -LiteralPath $SavePath)) {
    Write-Host "Не найдена папка телеметрии: $SavePath" -ForegroundColor Red
    Write-Host "Зайдите в мир хотя бы на пару минут — мод пишет телеметрию сам."
    exit 1
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $OutPath "EngineOptimizer-report-$stamp.txt"
$sections = 0

function Add-Section([string]$title, [string]$path, [int]$tail = 0, [int]$head = 0) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    $lines = @(Get-Content -LiteralPath $path -Encoding UTF8 -ErrorAction SilentlyContinue)
    if ($lines.Count -eq 0) { return }
    if ($tail -gt 0 -and $lines.Count -gt $tail) { $lines = $lines | Select-Object -Last $tail }
    if ($head -gt 0 -and $lines.Count -gt $head) { $lines = $lines | Select-Object -First $head }

    Add-Content -LiteralPath $out -Value "`n===== $title =====" -Encoding UTF8
    Add-Content -LiteralPath $out -Value "($path)" -Encoding UTF8
    $lines | Add-Content -LiteralPath $out -Encoding UTF8
    $script:sections++
    Write-Host ("  + {0}" -f $title) -ForegroundColor Green
}

Remove-Item -LiteralPath $out -ErrorAction SilentlyContinue

Add-Content -LiteralPath $out -Value "EngineOptimizer — пакет диагностики (для отправки ассистенту)" -Encoding UTF8
Add-Content -LiteralPath $out -Value ("Собран: {0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')) -Encoding UTF8
if ($Minutes -gt 0) {
    Add-Content -LiteralPath $out -Value ("Окно: последние {0} минут (spikes/timeline)" -f $Minutes) -Encoding UTF8
}

Write-Host "Собираю телеметрию EngineOptimizer..." -ForegroundColor Cyan

# 1) главное: авто-сводка «от чего лагало» (мод пишет при выходе из мира)
Add-Section "АВТО-СВОДКА СЕССИИ (last-session.txt)" (Join-Path $SavePath 'last-session.txt')

# 2) последний живой срез
Add-Section "live.json (последний срез)" (Join-Path $SavePath 'live.json')

# 3) окружение сессии
Add-Section "session.json (окружение, моды)" (Join-Path $SavePath 'session.json')

# 4) спайки: последние 1500 строк (если задано окно — фильтр по времени)
$spikesPath = Join-Path $SavePath 'spikes.csv'
if ($Minutes -gt 0 -and (Test-Path -LiteralPath $spikesPath)) {
    $cutoff = (Get-Date).AddMinutes(-$Minutes).ToString('yyyy-MM-dd HH:mm:ss')
    $tmp = [System.IO.Path]::GetTempFileName()
    Get-Content -LiteralPath $spikesPath -Encoding UTF8 | Where-Object { $_ -eq '' -or $_ -notmatch '^\d{4}-' -or $_.Substring(0, 19) -ge $cutoff } | Set-Content -LiteralPath $tmp -Encoding UTF8
    Add-Section "spikes.csv (окно $Minutes мин, кадры > 25 мс с причиной)" $tmp
    Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue
}
else {
    Add-Section "spikes.csv (последние 1500 кадров > 25 мс)" $spikesPath -tail 1500
}

# 5) таймлайн: последние 2000 строк (5-секундные срезы)
$tlPath = Join-Path $SavePath 'timeline.csv'
if ($Minutes -gt 0 -and (Test-Path -LiteralPath $tlPath)) {
    $cutoff = (Get-Date).AddMinutes(-$Minutes).ToString('yyyy-MM-dd HH:mm:ss')
    $tmp = [System.IO.Path]::GetTempFileName()
    Get-Content -LiteralPath $tlPath -Encoding UTF8 | Where-Object { $_ -eq '' -or $_ -notmatch '^\d{4}-' -or $_.Substring(0, 19) -ge $cutoff } | Set-Content -LiteralPath $tmp -Encoding UTF8
    Add-Section "timeline.csv (окно $Minutes мин)" $tmp
    Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue
}
else {
    Add-Section "timeline.csv (последние 2000 срезов)" $tlPath -tail 2000
}

# 6) логи tModLoader: хвост — исключения и ошибки модов
$logDirs = @(
    "$env:USERPROFILE\Documents\My Games\Terraria\tModLoader\Logs",
    "$env:USERPROFILE\Documents\My Games\Terraria\tModLoader-Logs"
)
foreach ($dir in $logDirs) {
    if (Test-Path -LiteralPath $dir) {
        $log = Get-ChildItem -LiteralPath $dir -Filter '*.log' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($log) {
            Add-Section ("tModLoader лог ({0}, последние 200 строк)" -f $log.Name) $log.FullName -tail 200
            break
        }
    }
}

if ($sections -eq 0) {
    Write-Host "Файлы телеметрии пусты — поиграйте хотя бы пару минут и выйдите из мира." -ForegroundColor Yellow
    Remove-Item -LiteralPath $out -ErrorAction SilentlyContinue
    exit 1
}

$size = [math]::Round((Get-Item -LiteralPath $out).Length / 1KB, 1)
Write-Host ""
Write-Host "===================================================================" -ForegroundColor Cyan
Write-Host ("ГОТОВО: {0} ({1} KB, разделителей: {2})" -f $out, $size, $sections) -ForegroundColor Cyan
Write-Host "===================================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "СЛЕДУЮЩИЙ ШАГ: перетащите этот файл в чат с ассистентом Arena" -ForegroundColor Yellow
Write-Host "(кнопка скрепки/плюс в чате) — ассистент разберёт и исправит лаги."
