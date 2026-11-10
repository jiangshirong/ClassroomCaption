param([switch]$SkipTests, [switch]$PortablePackage, [string]$CppCompiler = 'D:\jiangshirong packets\msys64\mingw32\bin\g++.exe')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$dotnetExe = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetExe)) {
    $installedDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $installedDotnet) { throw 'Install .NET SDK 10.0.403 or run scripts/bootstrap-sdk.py.' }
    $dotnetExe = $installedDotnet.Source
}
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\dotnet-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$buildOutput = Join-Path $projectRoot 'artifacts\development\'
$buildProperty = "-p:BaseOutputPath=$buildOutput"
$publishFolder = Join-Path $projectRoot 'dist\课堂字幕'
New-Item -ItemType Directory -Force -Path 'artifacts','.tools\nuget-local' | Out-Null
if (-not $SkipTests) {
    & $dotnetExe run --project 'tests\ClassroomCaption.Tests' -c Release $buildProperty -- --report 'artifacts\core-tests.json'
    if ($LASTEXITCODE -ne 0) { throw 'Core verification failed.' }
    & $dotnetExe run --project 'tests\ClassroomCaption.WindowsTests' -c Release $buildProperty -- --report 'artifacts\windows-tests.json'
    if ($LASTEXITCODE -ne 0) { throw 'Windows component verification failed.' }
    & $dotnetExe run --project 'tests\ClassroomCaption.WindowsTests' -c Release $buildProperty -- --floating-start-tests
    if ($LASTEXITCODE -ne 0) { throw 'Floating translation start verification failed.' }
    & $dotnetExe run --project 'tests\ClassroomCaption.WindowsTests' -c Release $buildProperty -- --runtime-diagnostics-tests
    if ($LASTEXITCODE -ne 0) { throw 'Runtime diagnostics verification failed.' }
}
& $dotnetExe build 'src\ClassroomCaption.App' -c Release $buildProperty --nologo
if($LASTEXITCODE -ne 0){throw 'Development build failed.'}
if(-not $PortablePackage){Write-Output ('Development build ready: '+(Join-Path $buildOutput 'Release\net10.0-windows\ClassroomCaption.exe'));return}
& (Join-Path $PSScriptRoot 'publish-compact.ps1') -OutputDirectory ('dist\课堂字幕-缓存便携版-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
return
