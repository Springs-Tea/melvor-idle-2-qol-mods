# One development round with nobody at the keyboard: builds the given mods with their development helpers,
# restarts the game, waits until the character is open, then optionally hands a request to a mod's helper
# and waits for the file that helper writes.
#   .\tools\dev-cycle.ps1 -Mod RecipeRates
#   .\tools\dev-cycle.ps1 -Mod DungeonCompletions -Request areas.request -Produces shot-areas.png
#   .\tools\dev-cycle.ps1 -Mod ItemSources -Into ItemSources -Request item.request -Text 'Big Bone' -Produces shot-item.png
#   .\tools\dev-cycle.ps1 -Mod ItemEncyclopedia -Into ItemEncyclopedia -Request enc.request -Text "open`nsearch=bow`nshot=bows" -Produces enc.done
#
# Combat Goals is always built along: its helper (mods\CombatGoals\Dev.cs) presses "Load" on the main menu and
# confirms the "Welcome back" window, which is what opens the character. -Into is the mod whose folder the
# request file goes to (the general requests are Combat Goals'). The helpers are compiled only by this
# script (-p:DevTools=true); run .\tools\restart-game.ps1 -Mod ... afterwards to play without them.
# Needs PowerShell 7 and the .NET 10 SDK.
param(
    [string[]]$Mod = @(),
    [string]$Request = '',
    [string]$Text = '',
    [string]$Into = 'CombatGoals',
    [string]$Produces = '',
    [string]$GameDir = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'game-dir.ps1')
$GameDir = Get-GameDir $GameDir
$mods = @(@(Resolve-ModNames $Mod) + 'CombatGoals' | Select-Object -Unique)
$folder = Join-Path $GameDir "mods\$Into"
$log = Join-Path $GameDir 'MelvorModLoader.log'
if ($Produces) { Remove-Item (Join-Path $folder $Produces) -ErrorAction SilentlyContinue }

# Never cut this script's output with "Select-Object -First": that ends the script itself.
$null = & (Join-Path $PSScriptRoot 'restart-game.ps1') -Mod $mods -Configuration Debug -DevTools -GameDir $GameDir

$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline -and -not (Select-String -Path $log -Pattern 'Character opened' -Quiet)) { Start-Sleep -Seconds 2 }
if (-not (Select-String -Path $log -Pattern 'Character opened' -Quiet)) { Write-Warning 'The character did not open within two minutes.' }

if ($Request) {
    Start-Sleep -Seconds 6
    Set-Content (Join-Path $folder $Request) $Text -NoNewline
    if ($Produces) {
        $deadline = (Get-Date).AddSeconds(90)
        while ((Get-Date) -lt $deadline -and -not (Test-Path (Join-Path $folder $Produces))) { Start-Sleep -Seconds 1 }
        if (-not (Test-Path (Join-Path $folder $Produces))) { Write-Warning "$Produces was not written within 90 seconds." }
        Start-Sleep -Seconds 1
    }
}
Get-Content $log | Select-Object -Skip 3
