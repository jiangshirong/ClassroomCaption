param([string]$Compiler='D:\jiangshirong packets\msys64\mingw32\bin\g++.exe')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not(Test-Path -LiteralPath $Compiler)){throw 'An x86 MinGW-w64 compiler is required.'}
$previousPath=$env:PATH
try {
    $env:PATH=(Split-Path -Parent $Compiler)+';'+$previousPath
    $machine=& $Compiler -dumpmachine
    if($machine -notlike 'i686-*'){throw 'The independent probe must remain x86.'}
    $source=Join-Path $root 'native\ClassroomProbe\main.cpp'
    $output=Join-Path $root 'native\ClassroomProbe\win-x86\ClassroomProbe.exe'
    & $Compiler -std=c++17 -O2 -static -static-libgcc -static-libstdc++ -municode -mwindows -finput-charset=UTF-8 $source -o $output -lole32 -luuid -lpropsys -lwinhttp -lshell32 -lksuser
    if($LASTEXITCODE -ne 0){throw 'Probe build failed.'}
    Write-Output ('Native probe rebuilt: '+$output)
} finally {$env:PATH=$previousPath}
