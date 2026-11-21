# Closes the game (gracefully, so it saves), optionally rebuilds a mod, starts the game again through Steam
# and prints the loader's log. The mod DLL is locked while the game runs, so the build happens in between.
#   .\tools\restart-game.ps1 -Mod CombatGoals -Configuration Debug
param(
    [string]$Mod = '',
    [string]$Configuration = 'Release',
    [string]$GameDir = 'D:\Steam\steamapps\common\Melvor Idle 2',
    [int]$AppId = 3218350
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$p = Get-Process Melvor2 -ErrorAction SilentlyContinue
if ($p) {
    $p.CloseMainWindow() | Out-Null
    if (-not $p.WaitForExit(30000)) { throw 'The game did not close within 30 seconds.' }
    Start-Sleep -Seconds 1
}

foreach ($name in ($Mod -split ',' | Where-Object { $_ })) {
    dotnet build (Join-Path $root "mods\$name\$name.csproj") -c $Configuration --nologo -v q -p:GameDir="$GameDir" 2>&1 |
        Select-Object -Last 12
    if ($LASTEXITCODE -ne 0) { Write-Warning "Build of $name failed; starting the game with what was there before." }
}

# After the game closes, Steam syncs its cloud saves and drops start requests until it is done (sometimes
# over a minute): keep asking until the game comes up.
foreach ($attempt in 1..6) {
    Start-Sleep -Seconds 6
    # steam.exe itself, not the steam:// link: the link's requests did not always reach Steam.
    $steam = Join-Path ($GameDir -replace '\\steamapps\\common\\.*$', '') 'steam.exe'
    if (Test-Path $steam) { Start-Process $steam -ArgumentList '-applaunch', $AppId } else { Start-Process "steam://rungameid/$AppId" }
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline -and -not (Get-Process Melvor2 -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 500 }
    if (Get-Process Melvor2 -ErrorAction SilentlyContinue) { break }
}
if (-not (Get-Process Melvor2 -ErrorAction SilentlyContinue)) { throw 'The game did not start.' }
Start-Sleep -Seconds 12
Get-Content (Join-Path $GameDir 'MelvorModLoader.log')
