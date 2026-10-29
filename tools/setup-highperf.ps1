# setup-highperf.ps1 — подготовка Windows к максимальному FPS (одноразовая).
#
# Запуск от администратора:
#   powershell -ExecutionPolicy Bypass -File setup-highperf.ps1
# Откат всех изменений:
#   powershell -ExecutionPolicy Bypass -File setup-highperf.ps1 -Undo
#
# Что делает (всё обратимо):
#  1. План питания «Высокая производительность» — процессор не засыпает
#     между кадрами, турбобуст доступен сразу. Сам буст (до 3.1 ГГц)
#     управляется BIOS/прошивкой: из игры до него не дотянуться, но
#     агрессивное энергосбережение ОС — главный тормоз на ровном месте.
#  2. PCIe Link State Power Management = Off (питание от сети) — меньше
#     микрозадержек GPU.
#  3. Game Mode = On — Windows не отдаёт ядра фоновым задачам в игре.
#
# Чего НЕ делает: не трогает BIOS, не разгоняет, не меняет частоты,
# не правит реестр за пределами GameBar.

param([switch]$Undo)

# --- проверка прав администратора ---
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Нужен запуск от администратора (правый клик по PowerShell -> Запуск от имени администратора)." -ForegroundColor Red
    exit 1
}

$highPerf = '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c'
$balanced = '381b4222-f694-41f0-9685-ff5bb260df2e'

if ($Undo) {
    powercfg /setactive $balanced
    powercfg -setacvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM 2
    Write-Host "Откат выполнен: план 'Сбалансированный', PCIe ASPM восстановлен." -ForegroundColor Green
    exit 0
}

# 1) план питания
$existing = powercfg /list | Select-String $highPerf
if (-not $existing) {
    powercfg /duplicatescheme $highPerf | Out-Null
}
powercfg /setactive $highPerf
Write-Host "[1/3] План питания: Высокая производительность" -ForegroundColor Green

# 2) PCIe ASPM off (питание от сети)
powercfg -setacvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM 0
powercfg /setactive SCHEME_CURRENT
Write-Host "[2/3] PCIe Link State PM: Off (от сети)" -ForegroundColor Green

# 3) Game Mode
$gb = 'HKCU:\Software\Microsoft\GameBar'
New-Item -Path $gb -Force | Out-Null
Set-ItemProperty -Path $gb -Name 'AllowAutoGameMode' -Value 1 -Type DWord
Set-ItemProperty -Path $gb -Name 'AutoGameModeEnabled' -Value 1 -Type DWord
Write-Host "[3/3] Game Mode: On" -ForegroundColor Green

Write-Host ""
Write-Host "Готово." -ForegroundColor Cyan
Write-Host "Турбобуст до 3.1 ГГц включается в BIOS (обычно уже включён)."
Write-Host "В игре мод дополнительно поднимает приоритет процесса (AboveNormal)."
Write-Host "Откат: setup-highperf.ps1 -Undo"
