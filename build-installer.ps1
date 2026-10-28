param(
    [string]$PackagePath = (Join-Path $PSScriptRoot 'Package'),
    [string]$OutputPath = (Join-Path $PSScriptRoot 'Emberville-Patch-Only-Installer.exe')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
# Root scripts are authoritative; refresh their packaged copies on every build.
foreach ($name in @('Patch.ps1', 'bundle_patch.py')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $PackagePath $name) -Force
}

# Explicit allowlist: never zip a working game or reconstructed payload directory.
$allowed = @('Patch.ps1', 'manifest.json', 'package-hashes.json', 'bundle_patch.py', 'ProfileProbe.dll', 'Mono.Cecil.dll', 'Stage1.exe', 'Stage2.exe', 'Stage3.exe', 'python', 'licenses', 'deltas')
foreach ($entry in Get-ChildItem -LiteralPath $PackagePath) {
    if ($entry.Name -notin $allowed) {
        throw "Unexpected package content: $($entry.Name)"
    }
}
if (Get-ChildItem -LiteralPath $PackagePath -Recurse -File | Where-Object { $_.Name -in @('Assembly-CSharp.dll', 'data.unity3d', 'Emberville.exe', 'UnityPlayer.dll', 'catalog.json', 'region-manifest.json') -or $_.Extension -in @('.raw', '.png', '.jpg', '.asset', '.assets') }) {
    throw 'Game file found in distribution package'
}
$hashes = @(Get-ChildItem -LiteralPath $PackagePath -Recurse -File |
    Where-Object Name -ne 'package-hashes.json' |
    ForEach-Object {
        @{
            path = $_.FullName.Substring($PackagePath.Length + 1)
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
$hashes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $PackagePath 'package-hashes.json') -Encoding UTF8
$work = Join-Path ([IO.Path]::GetTempPath()) ('EmbervilleBuild-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $zip = Join-Path $work 'Payload.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($PackagePath, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    $source = Join-Path $work 'Installer.cs'
    [IO.File]::WriteAllText($source, ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Installer.cs')).Replace('PACKAGE_HASH', $hash)), [Text.UTF8Encoding]::new($false))
    $csc = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    & $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$OutputPath" "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')" "/resource:$zip,PatchPackage.zip" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll $source
    if ($LASTEXITCODE -ne 0) {
        throw 'Installer compilation failed'
    }
    (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash + '  ' + [IO.Path]::GetFileName($OutputPath) | Set-Content -LiteralPath ($OutputPath + '.sha256.txt') -Encoding ASCII
    Write-Output "Built $OutputPath"
}
finally {
    $full = [IO.Path]::GetFullPath($work)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($full.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($full).StartsWith('EmbervilleBuild-')) {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
