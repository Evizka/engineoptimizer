# analyze-session.ps1 — сводный отчёт по телеметрии EngineOptimizer (v0.8.4+).
#
# Читает timeline.csv и spikes.csv из папки телеметрии и печатает компактный
# отчёт: FPS в бою vs вне боя, причины спайков, топ виновников по снарядам
# и NPC, доминирующие update-секции. Результат можно просто скопировать
# и прислать — этого достаточно для прицельной следующей версии мода.
#
# Пример:
#   powershell -ExecutionPolicy Bypass -File analyze-session.ps1
#   powershell -ExecutionPolicy Bypass -File analyze-session.ps1 -Minutes 30

param(
    [string]$SavePath = "$env:USERPROFILE\Documents\My Games\Terraria\tModLoader\EngineOptimizer",
    [int]$Minutes = 0
)

$ErrorActionPreference = 'Stop'

function Read-CsvSafe([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $rows = @(Import-Csv -LiteralPath $path)
    if ($rows.Count -eq 0) { return $null }
    # отфильтровать мусорные строки (битые записи)
    return @($rows | Where-Object { $_.time -and $_.frame_ms })
}

function HasCol($row, [string]$col) {
    return ($row.PSObject.Properties.Name -contains $col)
}

function Avg($vals) {
    $v = @($vals | Where-Object { $_ -as [double] } | ForEach-Object { [double]$_ })
    if ($v.Count -eq 0) { return $null }
    return [math]::Round(($v | Measure-Object -Average).Average, 2)
}

function Max2($vals) {
    $v = @($vals | Where-Object { $_ -as [double] } | ForEach-Object { [double]$_ })
    if ($v.Count -eq 0) { return $null }
    return [math]::Round(($v | Measure-Object -Maximum).Maximum, 2)
}

$timeline = Read-CsvSafe (Join-Path $SavePath 'timeline.csv')
$spikes = Read-CsvSafe (Join-Path $SavePath 'spikes.csv')

if (-not $timeline -and -not $spikes) {
    Write-Host "В $SavePath нет timeline.csv / spikes.csv — поиграйте хотя бы пару минут." -ForegroundColor Yellow
    exit 1
}

if ($Minutes -gt 0 -and $timeline) {
    $cutoff = (Get-Date).AddMinutes(-$Minutes).ToString('yyyy-MM-dd HH:mm:ss')
    $timeline = @($timeline | Where-Object { $_.time -ge $cutoff })
    if ($spikes) { $spikes = @($spikes | Where-Object { $_.time -ge $cutoff }) }
    Write-Host "Окно: последние $Minutes минут." -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "================ ENGINE OPTIMIZER — СВОДКА СЕССИИ ================" -ForegroundColor Cyan

# ---------- timeline: бой vs вне боя ----------
if ($timeline) {
    $cols = $timeline[0].PSObject.Properties.Name
    $hasBoss = $cols -contains 'boss_perf'

    $fight = @($timeline | Where-Object { $_.boss_perf -eq '1' })
    $calm = @($timeline | Where-Object { $_.boss_perf -ne '1' })

    Write-Host ""
    Write-Host ("timeline.csv: {0} записей (5 c между)" -f $timeline.Count) -ForegroundColor White
    if ($hasBoss -and $fight.Count -gt 0) {
        Write-Host ("  БОЙ     ({0,4} з.): fps {1}  frame {2} ms  p95 {3} ms  upd {4} + draw {5} ms" -f `
            $fight.Count, (Avg $fight.fps_draw), (Avg $fight.frame_avg_ms), (Avg $fight.frame_p95_ms),
            (Avg $fight.cpu_update_ms), (Avg $fight.cpu_draw_ms))
        Write-Host ("  вне боя ({0,4} з.): fps {1}  frame {2} ms  p95 {3} ms  upd {4} + draw {5} ms" -f `
            $calm.Count, (Avg $calm.fps_draw), (Avg $calm.frame_avg_ms), (Avg $calm.frame_p95_ms),
            (Avg $calm.cpu_update_ms), (Avg $calm.cpu_draw_ms))
    }
    else {
        Write-Host ("  fps {0}  frame {1} ms  p95 {2} ms  upd {3} + draw {4} ms  (боёв в записи нет)" -f `
            (Avg $timeline.fps_draw), (Avg $timeline.frame_avg_ms), (Avg $timeline.frame_p95_ms),
            (Avg $timeline.cpu_update_ms), (Avg $timeline.cpu_draw_ms))
    }

    # доминирующие update-секции (среднее по всей записи)
    Write-Host "  update-секции (мс, среднее):" -ForegroundColor White
    foreach ($c in 'upd_players', 'upd_npcs', 'upd_gores', 'upd_proj', 'upd_items', 'upd_dust', 'upd_time', 'upd_world', 'upd_inv', 'npc_ms', 'proj_ms', 'dust_ms', 'light', 'tiles_walls', 'bg') {
        if ($cols -contains $c) {
            $a = Avg $timeline.$c
            if ($a -ne $null -and $a -gt 0.05) {
                Write-Host ("    {0,-12} {1,8}" -f $c, $a)
            }
        }
    }

    # среднее число сущностей
    $ents = "  сущности: dust {0}  proj {1}  npc {2}" -f (Avg $timeline.dust), (Avg $timeline.proj), (Avg $timeline.npc)
    if ($cols -contains 'proj_top') {
        $topProj = @($timeline | Where-Object { $_.proj_top } | Group-Object proj_top | Sort-Object Count -Descending | Select-Object -First 3)
        foreach ($g in $topProj) {
            $ents += "`n    proj_top [$($g.Count)x]: $($g.Name)"
        }
    }
    if ($cols -contains 'npc_top') {
        $topNpc = @($timeline | Where-Object { $_.npc_top } | Group-Object npc_top | Sort-Object Count -Descending | Select-Object -First 3)
        foreach ($g in $topNpc) {
            $ents += "`n    npc_top  [$($g.Count)x]: $($g.Name)"
        }
    }
    Write-Host $ents -ForegroundColor Gray
}

# ---------- spikes ----------
if ($spikes) {
    Write-Host ""
    Write-Host ("spikes.csv: {0} кадров > 25 мс" -f $spikes.Count) -ForegroundColor White
    $byCause = $spikes | Group-Object cause | Sort-Object Count -Descending
    foreach ($g in $byCause) {
        $rows = @($g.Group)
        Write-Host ("  {0,-8} {1,5} шт.  frame avg {2}  max {3}  unacc {4}" -f `
            $g.Name, $rows.Count, (Avg $rows.frame_ms), (Max2 $rows.frame_ms), (Avg $rows.unacc_ms))
    }

    $cols = $spikes[0].PSObject.Properties.Name
    if ($cols -contains 'proj_top') {
        $topProj = @($spikes | Where-Object { $_.proj_top } | Group-Object proj_top | Sort-Object Count -Descending | Select-Object -First 3)
        if ($topProj.Count -gt 0) {
            Write-Host "  снаряды в спайках:" -ForegroundColor White
            foreach ($g in $topProj) { Write-Host ("    [$($g.Count)x] $($g.Name)") }
        }
    }
    if ($cols -contains 'npc_top') {
        $topNpc = @($spikes | Where-Object { $_.npc_top } | Group-Object npc_top | Sort-Object Count -Descending | Select-Object -First 3)
        if ($topNpc.Count -gt 0) {
            Write-Host "  NPC в спайках:" -ForegroundColor White
            foreach ($g in $topNpc) { Write-Host ("    [$($g.Count)x] $($g.Name)") }
        }
    }

    $bosses = @($spikes | Where-Object { $_.bosses } | Group-Object bosses | Sort-Object Count -Descending | Select-Object -First 3)
    if ($bosses.Count -gt 0) {
        Write-Host "  боссы в спайках:" -ForegroundColor White
        foreach ($g in $bosses) { Write-Host ("    [$($g.Count)x] $($g.Name)") }
    }
}

Write-Host ""
Write-Host "===================================================================" -ForegroundColor Cyan
Write-Host "Источник: $SavePath"
