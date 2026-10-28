param([ValidateSet('Install','Undo','Check','Launch')][string]$Action='Check',[string]$GamePath,[switch]$CloneSaves)
$ErrorActionPreference='Stop'
if(!$GamePath){$GamePath=Read-Host 'Paste the folder containing Emberville.exe (no quotes needed)'}
$GamePath=$GamePath.Trim('"')
$game=(Resolve-Path -LiteralPath $GamePath).Path
function Target([string]$relative){
 $full=[IO.Path]::GetFullPath((Join-Path $game $relative))
 if(!$full.StartsWith($game.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe target path'}
 $cursor=$full
 while($cursor -and $cursor.Length -ge $game.Length){if(Test-Path -LiteralPath $cursor){if((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Linked paths are not supported: $cursor"}};$cursor=Split-Path $cursor -Parent}
 return $full
}
function Hash([string]$path){$stream=[IO.File]::OpenRead($path);$sha=[Security.Cryptography.SHA256]::Create();try{return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$sha.Dispose();$stream.Dispose()}}
function CopyVerified([string]$source,[string]$destination,[string]$expected){
 New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force|Out-Null
 Copy-Item -LiteralPath $source -Destination $destination -Force
 if((Hash $destination) -ne $expected){throw "Verification failed: $destination"}
}
$exe=Target 'Emberville.exe'
if(!(Test-Path -LiteralPath $exe)){throw 'Emberville.exe was not found in that folder.'}
if(Get-Process Emberville -ErrorAction SilentlyContinue){throw 'Close Emberville before using this installer.'}
$manifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Raw|ConvertFrom-Json
$stateFolder=Target '.EmbervilleLoadingPatch'
$stateFile=Target '.EmbervilleLoadingPatch/state.json'
function SaveState($state){$temp=$stateFile+'.tmp';$state|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $temp -Encoding UTF8;Move-Item -LiteralPath $temp -Destination $stateFile -Force}
function Restore($state){
 # Validate every backup and destination before restoring anything.
 foreach($row in $state.files){
  $dest=Target $row.path
  if($row.existed -and (Hash (Target ('.EmbervilleLoadingPatch/backup/'+$row.path))) -ne $row.before){throw "Backup is damaged: $($row.path)"}
  if(Test-Path -LiteralPath $dest){$current=Hash $dest;if($state.status -ne 'installing' -and $current -ne $row.after -and $current -ne $row.before){throw "File changed after installation; refusing to overwrite: $($row.path)"}}
 }
 foreach($row in $state.files){$dest=Target $row.path;if($row.existed){CopyVerified (Target ('.EmbervilleLoadingPatch/backup/'+$row.path)) $dest $row.before}elseif(Test-Path -LiteralPath $dest){Remove-Item -LiteralPath $dest}}
 $state.status='restored';SaveState $state
 Write-Output 'Original game files restored and verified. Backups, test saves and logs were retained.'
}
if($Action -eq 'Undo'){
 if(!(Test-Path -LiteralPath $stateFile)){throw 'No installation journal found in this game folder.'}
 $state=Get-Content -LiteralPath $stateFile -Raw|ConvertFrom-Json
 if($state.id -ne $manifest.id){throw 'Patch journal belongs to another package.'}
 Restore $state
 exit
}
if($Action -eq 'Launch'){
 if(!(Test-Path -LiteralPath $stateFile)){throw 'Install the patch first.'}
 $state=Get-Content -LiteralPath $stateFile -Raw|ConvertFrom-Json
 if($state.status -ne 'installed'){throw 'Patch installation is not complete.'}
 foreach($row in $manifest.files){if((Hash (Target $row.path)) -ne $row.sha256){throw "Installed file mismatch: $($row.path)"}}
 $log=Target 'EmbervillePatch/Player.log'
 Start-Process -FilePath $exe -WorkingDirectory $game -ArgumentList @('-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-logFile',('"'+$log+'"')) -WindowStyle Normal
 Write-Output 'Started the patched copy with isolated test saves.'
 exit
}
if((Hash (Target 'UnityPlayer.dll')) -ne $manifest.engine_sha256){throw 'Unsupported Unity engine build. No game files were changed.'}
$assembly=Hash (Target 'Emberville_Data/Managed/Assembly-CSharp.dll')
$assets=Hash (Target 'Emberville_Data/data.unity3d')
if(!(@($manifest.accepted_inputs|Where-Object {$_.assembly -eq $assembly -and $_.assets -eq $assets}).Count)){throw 'Unsupported or modified game build. This patch requires the exact tested demo files. No game files were changed.'}
$packageFiles=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'package-hashes.json') -Raw|ConvertFrom-Json
foreach($entry in $packageFiles){if((Hash (Join-Path $PSScriptRoot $entry.path)) -ne $entry.sha256){throw "Damaged installer component: $($entry.path)"}}
if($Action -eq 'Check'){Write-Output 'Compatible game and intact patch instructions. No files changed.';exit}
# Reconstruct in the private extraction folder before touching the game or creating backups.
if(([IO.DriveInfo]::new([IO.Path]::GetPathRoot($PSScriptRoot))).AvailableFreeSpace -lt 12GB){throw 'At least 12 GB of free temporary space is needed to rebuild the assets. No game files changed.'}
$payload=Join-Path $PSScriptRoot 'payload'
$managed=Join-Path $payload 'Emberville_Data/Managed'
New-Item -ItemType Directory -Path $managed -Force|Out-Null
$sourceAssembly=Target 'Emberville_Data/Managed/Assembly-CSharp.dll'
$helper=Join-Path $PSScriptRoot 'ProfileProbe.dll'
$finalAssembly=Join-Path $managed 'Assembly-CSharp.dll'
if($assembly -eq '9679EA815D0CF85DBA19D3D6867F0DB6B1762960D875E1D98DD82569952315FB'){
 $stage1=Join-Path $PSScriptRoot 'stage1.dll';$stage2=Join-Path $PSScriptRoot 'stage2.dll'
 & (Join-Path $PSScriptRoot 'Stage1.exe') $sourceAssembly $stage1 $helper
 if($LASTEXITCODE -ne 0){throw 'Assembly patch stage 1 failed. No game files changed.'}
 & (Join-Path $PSScriptRoot 'Stage2.exe') $stage1 $stage2 $helper (Split-Path $sourceAssembly -Parent)
 if($LASTEXITCODE -ne 0){throw 'Assembly patch stage 2 failed. No game files changed.'}
 $sourceAssembly=$stage2
}
& (Join-Path $PSScriptRoot 'Stage3.exe') $sourceAssembly $finalAssembly $helper
if($LASTEXITCODE -ne 0){throw 'Assembly patch stage 3 failed. No game files changed.'}
Copy-Item -LiteralPath $helper -Destination (Join-Path $managed 'ProfileProbe.dll')
$assetOutput=Join-Path $payload 'Emberville_Data/data.unity3d'
Write-Output 'Rebuilding optimized assets from your game. This one-time install needs about 12 GB of temporary space.'
& (Join-Path $PSScriptRoot 'python/python.exe') -B (Join-Path $PSScriptRoot 'bundle_patch.py') apply (Target 'Emberville_Data/data.unity3d') (Join-Path $PSScriptRoot 'deltas/original.json') $assetOutput (Join-Path $PSScriptRoot 'asset-work')
if($LASTEXITCODE -ne 0){throw 'Asset reconstruction failed. No game files changed.'}
foreach($row in $manifest.files){$null=Target $row.path;if((Hash (Join-Path $payload $row.path)) -ne $row.sha256){throw "Reconstruction verification failed: $($row.path)"}}

$reinstall=$false
if(Test-Path -LiteralPath $stateFolder){
 if(!(Test-Path -LiteralPath $stateFile)){throw 'Incomplete backup folder found. Preserve it and use a fresh game copy.'}
 $prior=Get-Content -LiteralPath $stateFile -Raw|ConvertFrom-Json
 if($prior.id -ne $manifest.id -or $prior.status -ne 'restored'){throw 'Patch already installed or recovery required. Use Undo first.'}
 foreach($row in $prior.files){$dest=Target $row.path;if($row.existed){if((Hash $dest) -ne $row.before -or (Hash (Target ('.EmbervilleLoadingPatch/backup/'+$row.path))) -ne $row.before){throw 'Original files or backups changed after Undo.'}}elseif(Test-Path -LiteralPath $dest){throw 'A patch-created file has reappeared after Undo.'}}
 $reinstall=$true
}
if(!$reinstall -and (Test-Path -LiteralPath (Target 'EmbervillePatch'))){throw 'EmbervillePatch already exists. Use a fresh copy to avoid overwriting test data.'}
$records=@()
foreach($row in $manifest.files){$dest=Target $row.path;$exists=Test-Path -LiteralPath $dest;$records+=@{path=$row.path;existed=$exists;before=$(if($exists){Hash $dest}else{$null});after=$row.sha256}}
if(!$reinstall){New-Item -ItemType Directory -Path $stateFolder|Out-Null}
# Back up all existing targets before any game file is replaced.
if(!$reinstall){foreach($row in $records){if($row.existed){CopyVerified (Target $row.path) (Target ('.EmbervilleLoadingPatch/backup/'+$row.path)) $row.before}}}
$state=@{id=$manifest.id;status='installing';files=$records}
SaveState $state
try {
 foreach($row in $manifest.files){CopyVerified (Join-Path (Join-Path $PSScriptRoot 'payload') $row.path) (Target $row.path) $row.sha256}
 $data=Target 'EmbervillePatch/TestUserData'
 New-Item -ItemType Directory -Path $data -Force|Out-Null
 if($CloneSaves -and !(Test-Path -LiteralPath (Join-Path $data 'Saves'))){
  $original=Join-Path $env:USERPROFILE 'AppData/LocalLow/CygnusCross/Emberville'
  if(Test-Path -LiteralPath (Join-Path $original 'Saves')){Copy-Item -LiteralPath (Join-Path $original 'Saves') -Destination (Join-Path $data 'Saves') -Recurse;Get-ChildItem -LiteralPath $original -Filter '*.png' -File|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $data}}
 }
 # Derive metadata from this user's own resources in a separate game process.
 Write-Output 'Building local map caches. The game will close automatically when finished.'
 $cacheLog=Target 'EmbervillePatch/cache-build.log'
 $cacheProcess=Start-Process -FilePath $exe -WorkingDirectory $game -ArgumentList @('-batchmode','-nographics','-patch-build-caches','-logFile',('"'+$cacheLog+'"')) -WindowStyle Hidden -PassThru
 if(!$cacheProcess.WaitForExit(300000)){Stop-Process -Id $cacheProcess.Id -Force;$cacheProcess.WaitForExit();throw 'Local cache build timed out; game files will be restored.'}
 $cacheProcess.Refresh()
 if($cacheProcess.ExitCode -ne 0){throw "Local cache build failed ($($cacheProcess.ExitCode)); inspect EmbervillePatch/cache-build.log."}
 foreach($property in $manifest.cache_hashes.PSObject.Properties){if((Hash (Target ('EmbervillePatch/'+$property.Name))) -ne $property.Value){throw "Local cache differs from tested metadata: $($property.Name)"}}
 $state.status='installed';SaveState $state
 Write-Output 'Installation complete. Use Launch game. Your normal saves were not modified.'
}catch {
 $failure=$_
 try{Restore ([pscustomobject]$state)}catch{Write-Warning ('Automatic rollback could not finish. Keep the backup folder and run Undo.cmd. '+$_)}
 throw $failure
}
