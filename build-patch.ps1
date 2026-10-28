# Rebuild our helper and patch tools against a local, supported game copy.
# Usage: .\build-patch.ps1 -GamePath 'C:\path\to\Emberville Demo'
# Requires a .NET SDK and .NET Framework 4.7.2 reference assemblies.
param(
    [Parameter(Mandatory = $true)]
    [string]$GamePath
)

$ErrorActionPreference = 'Stop'
$package = Join-Path $PSScriptRoot 'Package'
$managed = Join-Path (Resolve-Path -LiteralPath $GamePath).Path 'Emberville_Data/Managed'
$framework = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.7.2'
$sdk = (& dotnet --list-sdks | Select-Object -Last 1)
if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^([^ ]+) \[(.+)\]$') {
    throw 'Install a .NET SDK before building the patch.'
}
$compiler = Join-Path $Matches[2] ($Matches[1] + '/Roslyn/bincore/csc.dll')
foreach ($required in @($compiler, "$framework/mscorlib.dll", "$managed/Assembly-CSharp.dll")) {
    if (!(Test-Path -LiteralPath $required)) {
        throw "Missing build dependency: $required"
    }
}

function Compile([string[]]$Options) {
    & dotnet $compiler /nologo /nostdlib+ /optimize+ /langversion:latest @Options
    if ($LASTEXITCODE -ne 0) {
        throw 'Compilation failed. Do not publish this package.'
    }
}

# Keep source order stable: injected hooks and cached metadata rely on existing types.
$helperSources = @(
    'ProfileProbe', 'BenchHooks', 'Optimizer', 'FastImages',
    'FrameMonitor', 'LightMapData', 'RegionPrefabs'
) | ForEach-Object { Join-Path $PSScriptRoot "helper/$_.cs" }
$gameReferences = Get-ChildItem -LiteralPath $managed -Filter '*.dll' -File |
    Where-Object Name -ne 'ProfileProbe.dll' |
    ForEach-Object { '/reference:' + $_.FullName }
Compile (@('/target:library', "/out:$package/ProfileProbe.dll") + $gameReferences + $helperSources)

$toolReferences = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'Facades/netstandard.dll') |
    ForEach-Object { "/reference:$framework/$_" }
foreach ($stage in 1..3) {
    Compile (@('/target:exe', '/platform:x64', "/out:$package/Stage$stage.exe",
        "/reference:$package/Mono.Cecil.dll") + $toolReferences +
        @(Join-Path $PSScriptRoot "assembly/Stage$stage.cs"))
}

# Only the helper hash changes on a source rebuild. Game output hashes stay fixed
# so Install still rejects any unexpected change made by the rebuilt patch tools.
$manifestPath = Join-Path $package 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$helperEntry = $manifest.files | Where-Object path -eq 'Emberville_Data/Managed/ProfileProbe.dll'
if (@($helperEntry).Count -ne 1) {
    throw 'Expected exactly one helper entry in the package manifest.'
}
$helperEntry.sha256 = (Get-FileHash -LiteralPath "$package/ProfileProbe.dll" -Algorithm SHA256).Hash
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
& (Join-Path $PSScriptRoot 'build-installer.ps1')
