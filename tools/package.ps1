# Builds every mod in Release (without the development helpers) and writes one zip per mod into dist\.
# Each zip is made to be extracted into the game folder: it holds mods\<Mod>\<Mod>.dll and a README.txt.
# The game does not have to be closed: nothing is written into its folder.
#   .\tools\package.ps1                 all mods
#   .\tools\package.ps1 -Mod RecipeRates
param(
    [string]$Mod = '',
    [string]$GameDir = 'D:\Steam\steamapps\common\Melvor Idle 2'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -Confirm:$false }
New-Item -ItemType Directory -Force $stage | Out-Null

$projects = Get-ChildItem (Join-Path $root 'mods') -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "$($_.Name).csproj") } |
    Where-Object { -not $Mod -or ($Mod -split ',') -contains $_.Name }

foreach ($project in $projects) {
    $name = $project.Name
    $csproj = Join-Path $project.FullName "$name.csproj"
    $version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $out = Join-Path $stage "$name\mods\$name"
    dotnet build $csproj -c Release --nologo -v q "-p:GameDir=$GameDir" "-p:OutputPath=$out\" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Build of $name failed." }

    # Only the mod's own DLL ships: the game and the loader already have everything else.
    Get-ChildItem $out -File | Where-Object { $_.Name -ne "$name.dll" } | Remove-Item -Force -Confirm:$false
    Get-ChildItem $out -Directory | Remove-Item -Recurse -Force -Confirm:$false
    $readme = Join-Path $project.FullName 'README.txt'
    if (Test-Path $readme) { Copy-Item $readme (Join-Path $out 'README.txt') }

    $zip = Join-Path $dist "$name-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force -Confirm:$false }
    Compress-Archive -Path (Join-Path $stage "$name\mods") -DestinationPath $zip
    '{0,-22} {1,8:N0} bytes  {2}' -f "$name $version", (Get-Item $zip).Length, $zip
}
Remove-Item $stage -Recurse -Force -Confirm:$false
