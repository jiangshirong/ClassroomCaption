param([string]$Compiler = 'D:\jiangshirong packets\msys64\ucrt64\bin\gcc.exe')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $projectRoot '.tools\rnnoise'
$archive = Join-Path $cache 'rnnoise-0.2.tar.gz'
$source = Join-Path $cache 'rnnoise-0.2'
$output = Join-Path $projectRoot 'native\rnnoise\win-x64'
New-Item -ItemType Directory -Force -Path $cache,$output | Out-Null
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest 'https://github.com/xiph/rnnoise/releases/download/v0.2/rnnoise-0.2.tar.gz' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '90fce4b00b9ff24c08dbfe31b82ffd43bae383d85c5535676d28b0a2b11c0d37') { throw 'RNNoise source checksum mismatch.' }
& tar -xzf $archive -C $cache
if ($LASTEXITCODE -ne 0) { throw 'RNNoise extraction failed.' }
if (-not (Test-Path -LiteralPath $Compiler)) { throw 'Pass -Compiler with a MinGW-w64 x64 gcc executable.' }
$previousPath = $env:PATH
try {
    $env:PATH = (Split-Path -Parent $Compiler) + ';' + $env:PATH
    $machine = & $Compiler -dumpmachine
    if ($machine -notlike 'x86_64-*') { throw 'RNNoise requires an x64 compiler.' }
    $units = @('denoise','rnn','pitch','kiss_fft','celt_lpc','nnet','nnet_default','parse_lpcnet_weights','rnnoise_data','rnnoise_tables') | ForEach-Object { Join-Path $source "src\$_.c" }
    # Portable scalar build: no AVX requirement, no MinGW runtime DLL dependency.
    & $Compiler -shared -O3 -s -std=c99 -static-libgcc -DRNNOISE_BUILD -DDLL_EXPORT -I (Join-Path $source 'include') -I (Join-Path $source 'src') @units -lm -o (Join-Path $output 'rnnoise.dll')
    if ($LASTEXITCODE -ne 0) { throw 'RNNoise native build failed.' }
    Copy-Item -LiteralPath (Join-Path $source 'COPYING') -Destination (Join-Path $projectRoot 'native\rnnoise\COPYING') -Force
} finally { $env:PATH = $previousPath }
Get-Item -LiteralPath (Join-Path $output 'rnnoise.dll') | Select-Object Name,Length
