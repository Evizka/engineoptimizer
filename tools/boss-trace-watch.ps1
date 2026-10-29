# boss-trace-watch.ps1 — наблюдатель телеметрии EngineOptimizer v0.8.3.
#
# Следит за live.json (1 запись в секунду) и пишет построчную историю
# FPS/таймингов с флагом boss_perf: до/во время/после боя. После сессии
# выводит сравнение «бой vs обычный кадр» — чтобы увидеть эффект
# BossPerfMode (дым Calamity + heat distortion + storm effects).
#
# Пример:
#   powershell -ExecutionPolicy Bypass -File boss-trace-watch.ps1 -Seconds 600
#
# Останавливается по Ctrl+C или через -Seconds. Результат:
#   boss-trace-<дата>.csv рядом со скриптом.

param(
    [string]$SavePath = "$env:USERPROFILE\Documents\My Games\Terraria\tModLoader\EngineOptimizer",
    [int]$Seconds = 300,
    [string]$OutDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
$live = Join-Path $SavePath 'live.json'
if (-not (Test-Path -LiteralPath $live)) {
    Write-Host "Не найден $live — зайдите в мир, телеметрия появится через пару секунд." -ForegroundColor Yellow
    exit 1
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $OutDir "boss-trace-$stamp.csv"
$header = 'time,boss_perf,fps_draw,frame_ms,frame_p95_ms,cpu_update_ms,cpu_draw_ms,dust_ms,projectiles_ms,npc_ms,cursor_ms,gc_pause_ms_per_s,alloc_mb_per_s,smoke_hook,tile_hooks,proc_priority,projectiles,proj_top'
# ВАЖНО: -FilePath, не -PositionalPath/Path-алиас: у Out-File позиционный
# аргумент работает, но с явным -FilePath скрипт не ломается в старых
# режимах PS (известный фикс v0.8.2-скрипта).
Out-File -FilePath $out -Encoding utf8 -InputObject $header

Write-Host "Смотрю $live $Seconds с. Лог: $out" -ForegroundColor Cyan
Write-Host "Зовите босса — сравнение будет в конце." -ForegroundColor Cyan

$watch = New-Object System.Diagnostics.Stopwatch
$watch.Start()
$lastTs = ''
$lastBoss = -1
$switches = 0

while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
    Start-Sleep -Milliseconds 250
    try {
        $raw = Get-Content -LiteralPath $live -Raw -ErrorAction Stop
        if (-not $raw) { continue }
        $j = $raw | ConvertFrom-Json -ErrorAction Stop
        if ($j.ts -eq $lastTs) { continue }   # ещё не обновилась
        $lastTs = $j.ts
    }
    catch { continue }

    $boss = if ($j.boss_perf) { 1 } else { 0 }
    if ($lastBoss -ge 0 -and $boss -ne $lastBoss) {
        $switches++
        if ($boss) {
            Write-Host ("{0}  >>> БОЙ начался" -f $j.ts) -ForegroundColor Red
        }
        else {
            Write-Host ("{0}  <<< БОЙ закончился" -f $j.ts) -ForegroundColor Green
        }
        # v0.8.4: кто плодит снаряды (если аномально много)
        if ($j.projectile_top) {
            Write-Host ("    снаряды: {0}  ({1})" -f $j.projectiles, $j.projectile_top) -ForegroundColor Yellow
        }
    }
    $lastBoss = $boss

    $row = '{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17}' -f `
        $j.ts, $boss, $j.fps_draw, $j.frame_ms, $j.frame_p95_ms,
        $j.cpu_update_ms, $j.cpu_draw_ms, $j.dust_ms, $j.projectiles_ms, $j.npc_ms,
        $j.cursor_ms, $j.gc_pause_ms_per_s, $j.alloc_mb_per_s,
        ($(if ($j.boss_perf_smoke_hook) { 1 } else { 0 })),
        ($(if ($j.boss_perf_tile_hooks) { 1 } else { 0 })),
        ($j.proc_priority),
        ($j.projectiles), ('"{0}"' -f ($j.projectile_top -replace '"', '""'))
    Add-Content -LiteralPath $out -Value $row -Encoding utf8
}

# --- итоги ---
$rows = Import-Csv -LiteralPath $out | Where-Object { $_.fps_draw -as [double] }
$fight = @($rows | Where-Object { $_.boss_perf -eq '1' })
$calm  = @($rows | Where-Object { $_.boss_perf -eq '0' })

function Stat($r, $col) {
    if (-not $r) { return $null }
    $v = $r | ForEach-Object { [double]($_.$col) } | Sort-Object
    if (-not $v) { return $null }
    $avg = ($v | Measure-Object -Average).Average
    [pscustomobject]@{ Avg = [math]::Round($avg, 2); N = $v.Count }
}

Write-Host ""
Write-Host "===== ИТОГИ ($($rows.Count) записей, переходов боя: $switches) =====" -ForegroundColor Cyan
foreach ($col in 'fps_draw', 'frame_ms', 'cpu_update_ms', 'cpu_draw_ms') {
    $f = Stat $fight $col
    $c = Stat $calm $col
    $fs = if ($f) { "{0,8} (n={1})" -f $f.Avg, $f.N } else { '      —' }
    $cs = if ($c) { "{0,8} (n={1})" -f $c.Avg, $c.N } else { '      —' }
    Write-Host ("{0,-16} бой: {1}   вне боя: {2}" -f $col, $fs, $cs)
}
Write-Host ""
Write-Host "CSV: $out"
