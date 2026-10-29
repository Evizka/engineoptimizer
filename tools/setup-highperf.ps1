# setup-highperf.ps1 — подготовка Windows к максимальному FPS (одноразовая).
#
# Запуск от администратора:
#   powershell -ExecutionPolicy Bypass -File setup-highperf.ps1
# Откат всех изменений:
#   powershell -ExecutionPolicy Bypass -File setup-highperf.ps1 -Undo
# Дополнительно: увеличить стек потока tModLoader (совет из гайдов по FPS
# для тяжёлых модпаков; заметно помогает при глубоких цепочках вызовов
# модов — меньше переключений стека/страничных промахов):
#   powershell -ExecutionPolicy Bypass -File setup-highperf.ps1 -PatchStackSize
#   (путь к игре ищется сам; можно указать: -GamePath "C:\Games\tModLoader")
#
# Что делает (всё обратимо):
#  1. План питания «Высокая производительность» — процессор не засыпает
#     между кадрами, турбобуст доступен сразу. Сам буст (до 3.1 ГГц)
#     управляется BIOS/прошивкой: из игры до него не дотянуться, но
#     агрессивное энергосбережение ОС — главный тормоз на ровном месте.
#  2. PCIe Link State Power Management = Off (питание от сети) — меньше
#     микрозадержек GPU.
#  3. Game Mode = On — Windows не отдаёт ядра фоновым задачам в игре.
#  4. GameDVR (фоновая запись игр) = Off — Xbox Game Bar не пишет диск и
#     не ест кадры. Откат: -Undo (вернёт 1).
#
# Чего НЕ делает: не трогает BIOS, не разгоняет, не меняет частоты,
# не правит реестр за пределами GameBar/GameDVR.

param(
    [switch]$Undo,
    [switch]$PatchStackSize,
    [string]$GamePath = ""
)

# --- проверка прав администратора ---
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Нужен запуск от администратора (правый клик по PowerShell -> Запуск от имени администратора)." -ForegroundColor Red
    exit 1
}

# --- только патч стека: реестр не трогаем, права админа не нужны ---
if ($PatchStackSize) {
    # Ищем tModLoader: явно указанный путь -> Steam -> стандартные места.
    $candidates = @()
    if ($GamePath -ne "") { $candidates += $GamePath }
    $libraryVdf = Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\libraryfolders.vdf'
    if (Test-Path $libraryVdf) {
        $libs = Select-String -Path $libraryVdf -Pattern '"path"\s+"([^"]+)"' -AllMatches
        foreach ($m in $libs.Matches) {
            $p = $m.Groups[1].Value -replace '\\\\','\'
            $candidates += (Join-Path $p 'steamapps\common\tModLoader')
        }
    }
    $candidates += "$env:USERPROFILE\.steam\steam\steamapps\common\tModLoader"
    $candidates += "C:\Program Files (x86)\Steam\steamapps\common\tModLoader"
    $candidates += "C:\Program Files\Steam\steamapps\common\tModLoader"

    $tml = $null
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c 'tModLoader.runtimeconfig.json')) { $tml = $c; break }
    }
    if (-not $tml) {
        Write-Host "tModLoader не найден. Укажите путь: -GamePath 'C:\Games\tModLoader'" -ForegroundColor Red
        exit 1
    }

    $rc = Join-Path $tml 'tModLoader.runtimeconfig.json'
    $bak = $rc + '.bak'
    if (-not (Test-Path $bak)) { Copy-Item $rc $bak }
    $json = Get-Content $rc -Raw -Encoding UTF8

    # DEFAULT_STACK_SIZE задаётся в байтах; гайды по FPS для тяжёлых модпаков
    # рекомендуют 6 400 000 вместо 400 000 (автор тестировал 400k-6.4M).
    $newSize = 6400000
    if ($json -match 'DEFAULT_STACK_SIZE') {
        $json = $json -replace '(DEFAULT_STACK_SIZE["\s:=]+)\d+', ("`$1" + $newSize)
    } elseif ($json -match '"System.Threading.Thread.DefaultStackSize"\s*:\s*\d+') {
        $json = $json -replace '("System.Threading.Thread.DefaultStackSize"\s*:\s*)\d+', ("`$1" + $newSize)
    } else {
        Write-Host "В $rc не найден ключ DEFAULT_STACK_SIZE — файл не изменён (не та версия tModLoader?)." -ForegroundColor Yellow
        Write-Host "Содержимое configProperties можно посмотреть и поправить вручную; резервная копия: $bak"
        exit 0
    }

    # аккуратно сохраняем (без BOM, как ждёт .NET)
    [IO.File]::WriteAllText($rc, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "DEFAULT_STACK_SIZE -> $newSize в $rc" -ForegroundColor Green
    Write-Host "Резервная копия: $bak (вернуть: скопировать .bak обратно)" -ForegroundColor Cyan
    exit 0
}

$highPerf = '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c'
$balanced = '381b4222-f694-41f0-9685-ff5bb260df2e'

if ($Undo) {
    powercfg /setactive $balanced
    powercfg -setacvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM 2
    # GameDVR обратно
    $gc = 'HKCU:\System\GameConfigStore'
    if (Test-Path $gc) { Set-ItemProperty -Path $gc -Name 'GameDVR_Enabled' -Value 1 -Type DWord -ErrorAction SilentlyContinue }
    $gdvr = 'HKCU:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\GameDVR'
    if (Test-Path $gdvr) { Set-ItemProperty -Path $gdvr -Name 'AppCaptureEnabled' -Value 1 -Type DWord -ErrorAction SilentlyContinue }
    Write-Host "Откат выполнен: план 'Сбалансированный', PCIe ASPM восстановлен, GameDVR включён." -ForegroundColor Green
    Write-Host "(Патч DEFAULT_STACK_SIZE, если делался, откатывается копией .bak файла.)" -ForegroundColor DarkGray
    exit 0
}

# 1) план питания
$existing = powercfg /list | Select-String $highPerf
if (-not $existing) {
    powercfg /duplicatescheme $highPerf | Out-Null
}
powercfg /setactive $highPerf
Write-Host "[1/4] План питания: Высокая производительность" -ForegroundColor Green

# 2) PCIe ASPM off (питание от сети)
powercfg -setacvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM 0
powercfg /setactive SCHEME_CURRENT
Write-Host "[2/4] PCIe Link State PM: Off (от сети)" -ForegroundColor Green

# 3) Game Mode
$gb = 'HKCU:\Software\Microsoft\GameBar'
New-Item -Path $gb -Force | Out-Null
Set-ItemProperty -Path $gb -Name 'AllowAutoGameMode' -Value 1 -Type DWord
Set-ItemProperty -Path $gb -Name 'AutoGameModeEnabled' -Value 1 -Type DWord
Write-Host "[3/4] Game Mode: On" -ForegroundColor Green

# 4) v0.8.10: GameDVR off — фоновая запись игр (Xbox Game Bar) ест FPS
$gc = 'HKCU:\System\GameConfigStore'
New-Item -Path $gc -Force | Out-Null
Set-ItemProperty -Path $gc -Name 'GameDVR_Enabled' -Value 0 -Type DWord
$gdvr = 'HKCU:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\GameDVR'
New-Item -Path $gdvr -Force | Out-Null
Set-ItemProperty -Path $gdvr -Name 'AppCaptureEnabled' -Value 0 -Type DWord
Write-Host "[4/4] GameDVR (фоновая запись игр): Off" -ForegroundColor Green

Write-Host ""
Write-Host "Готово." -ForegroundColor Cyan
Write-Host "Турбобуст до 3.1 ГГц включается в BIOS (обычно уже включён)."
Write-Host "В игре мод дополнительно поднимает приоритет процесса (AboveNormal)."
Write-Host "Ещё советы из гайдов (в самой игре): освещение Color/Retro/White (не Trippy),"
Write-Host "frame skip Subtle, multi-core lighting On, качество Low, фоновые обои Off."
Write-Host "Тяжёлый модпак: setup-highperf.ps1 -PatchStackSize (стек потока 6.4M)."
Write-Host "Откат: setup-highperf.ps1 -Undo"
