# One development round: rebuild the given mods (Debug), restart the game, wait until the character is open,
# then optionally fire a request file of the Dev helper and wait for the file it produces.
#   .\tools\dev-cycle.ps1 -Mod CombatGoals,KillsInCombat -Request areas.request -Produces shot-areas.png
param(
    [string]$Mod = '',
    [string]$Request = '',
    [string]$Produces = 'shot.png',
    [string]$GameDir = 'D:\Steam\steamapps\common\Melvor Idle 2'
)
$dev = Join-Path $GameDir 'mods\CombatGoals'
$log = Join-Path $GameDir 'MelvorModLoader.log'
Remove-Item (Join-Path $dev $Produces), (Join-Path $dev 'shot.png') -ErrorAction SilentlyContinue

# Never cut this script's output with "Select-Object -First": that stops the script itself.
$null = & (Join-Path $PSScriptRoot 'restart-game.ps1') -Mod $Mod -Configuration Debug -GameDir $GameDir

$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline -and -not (Select-String -Path $log -Pattern 'Character opened' -Quiet)) { Start-Sleep -Seconds 2 }
if ($Request) {
    Start-Sleep -Seconds 6
    New-Item (Join-Path $dev $Request) -ItemType File -Force | Out-Null
}
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline -and -not (Test-Path (Join-Path $dev $Produces))) { Start-Sleep -Seconds 1 }
Start-Sleep -Seconds 1
Get-Content $log | Select-Object -Skip 3
