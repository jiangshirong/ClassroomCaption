param([switch]$Apply)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$candidates=@(
 '.tools\dotnet-sdk.zip','.tools\sdk-fast.zip','.tools\sdk-verified.zip',
 '.tools\check8','.tools\precheck8','.tools\icon-cache',
 'ClassroomCaption.dll','ClassroomCaption.Core.dll',
 'src\ClassroomCaption.App\artifacts','src\ClassroomCaption.Core\artifacts',
 'src\ClassroomCaption.App\bin','src\ClassroomCaption.App\obj',
 'src\ClassroomCaption.Core\bin','src\ClassroomCaption.Core\obj',
 'tests\ClassroomCaption.Tests\bin','tests\ClassroomCaption.Tests\obj',
 'tests\ClassroomCaption.WindowsTests\bin','tests\ClassroomCaption.WindowsTests\obj',
 'artifacts\single-file-release','artifacts\compact-release-final','artifacts\local-update-backups',
 'artifacts\trace-check','artifacts\floating-check','artifacts\ui-removal-check',
 'artifacts\compact-publish','artifacts\native',
 'artifacts\build\Release\net10.0-windows\win-x64'
)
$running=@(Get-CimInstance Win32_Process -Filter "Name='ClassroomCaption.exe'" | ForEach-Object {$_.ExecutablePath})
$manifest=@()
foreach($relative in $candidates){
 $path=[IO.Path]::GetFullPath((Join-Path $root $relative))
 if(-not $path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Cleanup target escaped workspace.'}
 if(-not(Test-Path -LiteralPath $path)){continue}
 $item=Get-Item -LiteralPath $path -Force
 if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cleanup cannot follow a linked path.'}
 if($running | Where-Object {$_ -and ($_.Equals($path,[StringComparison]::OrdinalIgnoreCase)-or $_.StartsWith($path+'\',[StringComparison]::OrdinalIgnoreCase))}){continue}
 # Refuse any link in the ancestry or subtree, and any classroom/session record.
 $parent=$item.DirectoryName;if($item.PSIsContainer){$parent=$item.Parent.FullName}
 while($parent -and $parent.Length -gt $root.Length){if((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked ancestor rejected.'};$parent=Split-Path -Parent $parent}
 $entries=if($item.PSIsContainer){@(Get-ChildItem -LiteralPath $path -Force -Recurse)}else{@($item)}
 if($entries | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}){throw 'Linked descendant rejected.'}
 if($entries | Where-Object {$_.Name -like '课堂-*.jsonl' -or $_.Name -like '课堂-*.txt'}){continue}
 $size=($entries | Where-Object {-not $_.PSIsContainer} | Measure-Object -Property Length -Sum).Sum
 $manifest+=[pscustomobject]@{path=$relative;bytes=[long]$size;removed=[bool]$Apply}
 if($Apply){Remove-Item -LiteralPath $path -Recurse -Force}
}
$folder=Join-Path $root 'artifacts\performance';New-Item -ItemType Directory -Force -Path $folder|Out-Null
$manifest|ConvertTo-Json -Depth 3|Set-Content -LiteralPath (Join-Path $folder 'cleanup-manifest.json') -Encoding UTF8
[pscustomobject]@{applied=[bool]$Apply;targets=$manifest.Count;bytes=($manifest|Measure-Object bytes -Sum).Sum}|ConvertTo-Json
