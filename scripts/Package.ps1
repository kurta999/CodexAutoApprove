param(
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$Version = 'local',
    [switch]$FrameworkDependent
)
$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$packageName = "CodexAutoApprove-$Version-win-x64"
if ($FrameworkDependent) { $packageName += '-framework-dependent' }
$packageRoot = Join-Path $projectRoot "publish\$packageName"
New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
$publishArgs = @('publish', (Join-Path $projectRoot 'CodexAutoApprove.csproj'), '--configuration', 'Release',
    '--runtime', 'win-x64', '--output', $packageRoot, '--disable-build-servers', '-p:DebugType=None',
    "-p:OutputPath=$projectRoot\artifacts\publish-build\$packageName\")
if ($FrameworkDependent) { $publishArgs += @('--self-contained', 'false') }
else { $publishArgs += @('--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') }
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $packageRoot -Force
New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot 'docs') | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\application.png') -Destination (Join-Path $packageRoot 'docs') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot 'assets') | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\app.png') -Destination (Join-Path $packageRoot 'assets') -Force
$required = @('CodexAutoApprove.exe', 'settings.ini', 'images\allow_once.png', 'images\allow_once_2.png', 'images\allow_once_3.png', 'README.md', 'docs\application.png', 'assets\app.png')
foreach ($file in $required) {
    if (!(Test-Path -LiteralPath (Join-Path $packageRoot $file))) { throw "Package is missing $file" }
}
$archive = Join-Path $projectRoot "publish\$packageName.zip"
Compress-Archive -Path "$packageRoot\*" -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText("$archive.sha256", "$hash  $packageName.zip`n")
Write-Host "Package: $archive"
Write-Host "SHA256:  $hash"
