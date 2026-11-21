# Checks what can be checked without the game: the mods cannot be built on a machine that does not have
# Melvor Idle 2, so this guards the rules of the repository instead. Runs on every pull request, and locally:
#   .\tools\check-repo.ps1          (PowerShell 7, Windows or Linux)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
$problems = [System.Collections.Generic.List[string]]::new()
function Fail([string]$message) { $problems.Add($message) }

try {
    # Everything git knows about or would add: tracked files plus new ones that are not ignored.
    $files = @(git ls-files) + @(git ls-files --others --exclude-standard) | Sort-Object -Unique

    # 1. Nothing from the game, and nothing built. A mod's DLL belongs in a release, never in the repository.
    $binary = '\.(dll|exe|pdb|pck|sav|so|dylib|zip|7z|rar|import|ctex|godot)$'
    foreach ($file in $files | Where-Object { $_ -match $binary }) {
        Fail "$file : built or game file. The repository holds source only."
    }
    foreach ($file in $files | Where-Object { $_ -match '(^|/)(bin|obj|dist|_save-backup)/' }) {
        Fail "$file : inside a folder that must stay out of git."
    }

    # 2. Pictures stay small enough to browse, and there are no stray large files.
    foreach ($file in $files) {
        $size = (Get-Item -LiteralPath $file -Force).Length   # -Force: dot files are hidden on Linux
        if ($size -gt 1MB) { Fail ('{0} : {1:N0} bytes. Keep files under 1 MB.' -f $file, $size) }
    }

    # 3. No paths into somebody's user folder, and no Steam ids.
    $text = $files | Where-Object { $_ -match '\.(cs|csproj|ps1|py|md|txt|yml|yaml|json|svg)$' -and $_ -notmatch '^third_party/' }
    foreach ($file in $text) {
        $content = Get-Content -LiteralPath $file -Raw
        if (-not $content) { continue }
        if ($content -match '(?i)[A-Z]:\\Users\\(?!<)[^\\\s"]+' -or $content -match '/home/[a-z][\w-]+/') {
            Fail "$file : contains a path into a user folder."
        }
        if ($content -match '\b7656119\d{10}\b') { Fail "$file : contains a Steam id." }
    }

    # 4. Every mod is complete, and compiles against the game without copying it.
    $mods = Get-ChildItem mods -Directory | Where-Object { Test-Path (Join-Path $_.FullName "$($_.Name).csproj") }
    if (-not $mods) { Fail 'No mod projects found under mods/.' }
    $readme = Get-Content README.md -Raw
    foreach ($mod in $mods) {
        $name = $mod.Name
        $project = [xml](Get-Content (Join-Path $mod.FullName "$name.csproj") -Raw)

        $version = $project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
        if ($version -notmatch '^\d+\.\d+\.\d+$') { Fail "mods/$name : <Version> is missing or not like 1.2.3." }

        $assembly = $project.Project.PropertyGroup.AssemblyName | Where-Object { $_ } | Select-Object -First 1
        if ($assembly -ne $name) { Fail "mods/$name : <AssemblyName> must be $name, the loader looks for $name.dll." }

        foreach ($reference in $project.Project.ItemGroup.Reference | Where-Object { $_ }) {
            if ($reference.Private -ne 'false') {
                Fail "mods/$name : reference $($reference.Include) needs <Private>false</Private>, or the game's DLL is copied into the mod."
            }
        }
        if ($project.Project.ItemGroup.PackageReference | Where-Object { $_ }) {
            Fail "mods/$name : package references are not supported, a mod ships as one DLL."
        }

        foreach ($needed in 'README.md', 'README.txt') {
            if (-not (Test-Path (Join-Path $mod.FullName $needed))) { Fail "mods/$name : $needed is missing." }
        }
        $shipped = Join-Path $mod.FullName 'README.txt'
        if ($version -and (Test-Path $shipped) -and (Get-Content $shipped -Raw) -notmatch [regex]::Escape($version)) {
            Fail "mods/$name : README.txt does not mention version $version."
        }
        if (-not (Test-Path "publish/nexus/$name.txt")) { Fail "publish/nexus/$name.txt is missing." }
        if ($readme -notmatch [regex]::Escape("mods/$name")) { Fail "README.md does not link mods/$name." }
    }

    # 5. Links between the pages of the repository lead somewhere.
    foreach ($page in $files | Where-Object { $_ -match '\.md$' -and $_ -notmatch '^third_party/' }) {
        $folder = Split-Path $page -Parent
        $content = Get-Content -LiteralPath $page -Raw
        $targets = @([regex]::Matches($content, '\]\(([^)\s]+)\)') | ForEach-Object { $_.Groups[1].Value }) +
                   @([regex]::Matches($content, '(?:src|href)="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
        foreach ($target in $targets | Sort-Object -Unique) {
            if ($target -match '^(https?:|mailto:|#)') { continue }
            $path = ($target -split '[#?]')[0]
            if (-not $path) { continue }
            $full = if ($folder) { Join-Path $folder $path } else { $path }
            if (-not (Test-Path -LiteralPath $full)) { Fail "$page : link to $target leads nowhere." }
        }
    }
}
finally { Pop-Location }

if ($problems.Count) {
    $problems | ForEach-Object { Write-Host "  x $_" }
    Write-Host "`n$($problems.Count) problem(s)."
    exit 1
}
Write-Host "Repository checks passed: $($mods.Count) mods, $($files.Count) files."
