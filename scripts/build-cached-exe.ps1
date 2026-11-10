param(
    [Parameter(Mandatory=$true)][string]$InnerExe,
    [Parameter(Mandatory=$true)][string]$OutputExe,
    [int]$BlockMiB=64,
    [string]$Compiler='D:\jiangshirong packets\msys64\ucrt64\bin\g++.exe'
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$inputPath=[IO.Path]::GetFullPath($InnerExe)
$outputPath=[IO.Path]::GetFullPath($OutputExe)
if(-not $outputPath.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output must remain inside the project.'}
if(Test-Path -LiteralPath $outputPath){throw 'Choose a new output executable.'}
if(-not(Test-Path -LiteralPath $inputPath)){throw 'Inner self-contained EXE not found.'}
if($BlockMiB -lt 1 -or $BlockMiB -gt 64){throw 'LZMS block size must be between 1 and 64 MiB.'}
if(-not(Test-Path -LiteralPath $Compiler)){throw 'An x64 MinGW-w64 compiler is required.'}
$windres=Join-Path (Split-Path -Parent $Compiler) 'windres.exe'
if(-not(Test-Path -LiteralPath $windres)){throw 'Resource compiler not found.'}
$stage=Join-Path $root ('artifacts\cache-package-build\'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $stage,(Split-Path -Parent $outputPath)|Out-Null
$originalLocation=Get-Location
$originalPath=$env:PATH
try{
    $env:PATH=(Split-Path -Parent $Compiler)+';'+$env:PATH
    $machine=& $Compiler -dumpmachine
    if($machine -notlike 'x86_64-*'){throw 'Cache launcher must be x64.'}
    if(-not('CachePackageCodec' -as [type])){Add-Type -Path (Join-Path $root 'native\CacheLauncher\PackageCodec.cs')}
    $compression=[CachePackageCodec]::Build($inputPath,(Join-Path $stage 'payload.lzms'),(Join-Path $stage 'metadata.txt'),[uint32]($BlockMiB*1024*1024))
    $compression|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $stage 'compression-report.json') -Encoding UTF8
    Copy-Item -LiteralPath (Join-Path $root 'native\CacheLauncher\main.cpp') -Destination (Join-Path $stage 'launcher.cpp')
    Copy-Item -LiteralPath (Join-Path $root 'native\CacheLauncher\app.manifest') -Destination (Join-Path $stage 'app.manifest')
    Copy-Item -LiteralPath (Join-Path $root 'src\ClassroomCaption.App\Assets\AppLogo.ico') -Destination (Join-Path $stage 'logo.ico')
    @'
1 ICON "logo.ico"
1 24 "app.manifest"
101 RCDATA "payload.lzms"
102 RCDATA "metadata.txt"
'@ | Set-Content -LiteralPath (Join-Path $stage 'launcher.rc') -Encoding ASCII
    Set-Location -LiteralPath $stage
    & $windres --codepage=65001 launcher.rc -O coff -o resources.o
    if($LASTEXITCODE -ne 0){throw 'Resource compilation failed.'}
    & $Compiler -std=c++17 -O2 -s -ffunction-sections -fdata-sections '-Wl,--gc-sections' -static -static-libgcc -static-libstdc++ -municode -mwindows -finput-charset=UTF-8 launcher.cpp resources.o -o launcher.exe -lcabinet -lbcrypt -lole32 -lshell32 -luuid
    if($LASTEXITCODE -ne 0){throw 'Cache launcher build failed.'}
    [IO.File]::Copy((Join-Path $stage 'launcher.exe'),$outputPath,$false)
    $report=[ordered]@{output=$outputPath;innerBytes=$compression.InputBytes;compressedPayloadBytes=$compression.CompressedBytes;
        executableBytes=(Get-Item -LiteralPath $outputPath).Length;sha256=$compression.Sha256;
        compressionMs=$compression.CompressionMs;decompressionMs=$compression.DecompressionMs;
        blockMiB=$BlockMiB;byteExactRoundtrip=$true;selfContained=$true;cacheEnabled=$true}
    $report|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $stage 'build-report.json') -Encoding UTF8
    $report|ConvertTo-Json
}finally{
    Set-Location -LiteralPath $originalLocation
    $env:PATH=$originalPath
    # Only this invocation's generated intermediates; reports and original inputs remain.
    foreach($name in @('payload.lzms','resources.o','launcher.exe')){
        $temporary=[IO.Path]::GetFullPath((Join-Path $stage $name))
        if(-not $temporary.StartsWith($stage+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid intermediate path.'}
        if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}
    }
}
