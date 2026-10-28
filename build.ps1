param(
	[string]$TerrariaSteamPath = "C:\Program Files (x86)\Steam\steamapps\common\tModLoader\",
	[string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

# tModLoader ships a .NET runtime, but building mods needs the SDK.
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue

if (-not $dotnet) {
	$candidates = @(
		"$env:ProgramFiles\dotnet\dotnet.exe",
		"${env:ProgramFiles(x86)}\dotnet\dotnet.exe",
		"$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
	) | Where-Object { Test-Path $_ }

	foreach ($candidate in $candidates) {
		& $candidate --list-sdks | Out-Null
		if ($LASTEXITCODE -eq 0) {
			$dotnet = Get-Item $candidate
			break
		}
	}
}

if (-not $dotnet) {
	throw "dotnet SDK not found. Install .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0"
}

$sdk = & $dotnet.Source --list-sdks
if (-not ($sdk | Select-String -Pattern '^8\.')) {
	throw ".NET 8 SDK is required. Installed SDKs:`n$sdk"
}

& $dotnet.Source build "$PSScriptRoot\EngineOptimizer.csproj" `
	-c $Configuration `
	"-p:TerrariaSteamPath=$TerrariaSteamPath"

exit $LASTEXITCODE
