param([switch]$BuildOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $projectRoot '.tools\dotnet'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\dotnet-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$app = Join-Path $projectRoot 'artifacts\development\Release\net10.0-windows\ClassroomCaption.exe'
$log = Join-Path $projectRoot 'artifacts\development-start.log'
$buildGate = New-Object System.Threading.Mutex($false,'Local\ClassroomCaption.DevelopmentBuild')
$ownsBuildGate = $false
try {
    try {$ownsBuildGate=$buildGate.WaitOne(30000)}catch [System.Threading.AbandonedMutexException] {$ownsBuildGate=$true}
    if(-not $ownsBuildGate){throw 'Another development build is still running.'}
    Set-Location -LiteralPath $projectRoot
    $running = Get-CimInstance Win32_Process -Filter "Name='ClassroomCaption.exe'" | Where-Object { $_.ExecutablePath -eq $app }
    if ($running) { if($BuildOnly){exit 3}; Start-Process -FilePath $app -WorkingDirectory $projectRoot | Out-Null;exit 0 }
    if (-not $running) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $log) | Out-Null
        & (Join-Path $env:DOTNET_ROOT 'dotnet.exe') build (Join-Path $projectRoot 'src\ClassroomCaption.App\ClassroomCaption.App.csproj') -c Release "-p:BaseOutputPath=$projectRoot/artifacts/development/" --nologo -v quiet *> $log
        if ($LASTEXITCODE -ne 0) { throw 'Development build failed. See artifacts\development-start.log.' }
    }
    if(-not $BuildOnly){Start-Process -FilePath $app -WorkingDirectory $projectRoot | Out-Null}
} catch {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show("Development startup failed. Please close the app and retry. Details: $log", 'Classroom Caption') | Out-Null
    exit 1
}
finally {if($ownsBuildGate){$buildGate.ReleaseMutex()};$buildGate.Dispose()}
