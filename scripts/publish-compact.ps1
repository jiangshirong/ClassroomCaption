param(
    [switch]$IncludeSavedConfiguration,
    [string]$DataDirectory = 'dist\课堂字幕\data',
    [string]$OutputDirectory = 'dist\课堂字幕-精简版'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$dotnetExe = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\dotnet-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$stage = Join-Path $projectRoot ('artifacts\compact-publish\' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'payload'
$standalone = Join-Path $stage 'standalone'
$verifyData = Join-Path $stage 'verification-data'
$output = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
if (-not $output.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must stay inside this workspace.' }
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory to preserve the previous release.' }
New-Item -ItemType Directory -Force -Path $payload,$standalone,$verifyData | Out-Null
$credentialInput = Join-Path $stage 'initial-credentials.json'
$settingsInput = Join-Path $stage 'initial-settings.json'
$standaloneExe = Join-Path $standalone 'ClassroomCaption.exe'
$verificationCredentials = Join-Path $verifyData 'credentials.json'
$arguments = @('publish','src\ClassroomCaption.App','-c','Release','-p:PublishProfile=CachePayload','-p:DebugType=None','-p:DebugSymbols=false','-p:CopyOutputSymbolsToPublishDirectory=false','-o',$payload)
try {
    if ($IncludeSavedConfiguration) {
        try {
            $savedRoot = if([IO.Path]::IsPathRooted($DataDirectory)){[IO.Path]::GetFullPath($DataDirectory)}else{[IO.Path]::GetFullPath((Join-Path $projectRoot $DataDirectory))}
            $savedCredentials = Get-Content -Raw -LiteralPath (Join-Path $savedRoot 'credentials.json') | ConvertFrom-Json
            $savedSettings = Get-Content -Raw -LiteralPath (Join-Path $savedRoot 'settings.json') | ConvertFrom-Json
        } catch { throw 'Cannot read the saved local configuration.' }
        if ([string]::IsNullOrWhiteSpace($savedCredentials.AliyunKey) -or [string]::IsNullOrWhiteSpace($savedSettings.WorkspaceId)) { throw 'The saved Aliyun connection is incomplete.' }
        # Carry only the current model credential, never legacy or billing credentials.
        [ordered]@{AliyunKey=$savedCredentials.AliyunKey} | ConvertTo-Json | Set-Content -LiteralPath $credentialInput -Encoding UTF8
        $savedSettings.InputDeviceId = ''
        $savedSettings.OutputDirectory = ''
        $savedSettings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsInput -Encoding UTF8
        $arguments += @("-p:InitialCredentialsFile=$credentialInput", "-p:InitialSettingsFile=$settingsInput")
    }
    & $dotnetExe @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Compact publication failed.' }
    if (Test-Path -LiteralPath (Join-Path $payload 'rnnoise.dll')) { throw 'RNNoise was not bundled into the executable.' }
    $wrappedExe=Join-Path $stage 'wrapped\ClassroomCaption.exe'
    & (Join-Path $PSScriptRoot 'build-cached-exe.ps1') -InnerExe (Join-Path $payload 'ClassroomCaption.exe') -OutputExe $wrappedExe
    if(-not(Test-Path -LiteralPath $wrappedExe)){throw 'Portable cache launcher was not produced.'}
    Copy-Item -LiteralPath $wrappedExe -Destination (Join-Path $payload 'ClassroomCaption.exe') -Force
    Copy-Item -LiteralPath (Join-Path $payload 'ClassroomCaption.exe') -Destination $standaloneExe
    $report = Join-Path $stage 'installation-verification.json'
    $processArgs = @('--program-cache-dir',('"'+(Join-Path $verifyData 'program-cache')+'"'),'--bootstrap-report',('"'+(Join-Path $stage 'cache-verification.json')+'"'),'--bootstrap-no-dialog','--data-dir', ('"' + $verifyData + '"'), '--verify-install', ('"' + $report + '"'))
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $standaloneExe -ArgumentList $processArgs -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Standalone verification timed out.' }
    $watch.Stop()
    if (-not (Test-Path -LiteralPath $report)) { throw 'Standalone verification produced no report.' }
    $check = Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or -not $check.passed) { throw ('Standalone verification failed: ' + $check.errorType) }
    if ($IncludeSavedConfiguration) {
        $seeded = Get-Content -Raw -LiteralPath $verificationCredentials | ConvertFrom-Json
        $seededSettings = Get-Content -Raw -LiteralPath (Join-Path $verifyData 'settings.json') | ConvertFrom-Json
        if ($seeded.AliyunKey -cne $savedCredentials.AliyunKey -or $seededSettings.WorkspaceId -cne $savedSettings.WorkspaceId) { throw 'Embedded connection configuration mismatch.' }
    }
    Copy-Item -LiteralPath 'docs\使用说明.txt','LUCIDE-LICENSE.txt' -Destination $payload
    Copy-Item -LiteralPath 'src\ClassroomCaption.App\Assets\Fonts\OFL.txt' -Destination (Join-Path $payload 'NotoSansSC-OFL.txt')
    foreach ($name in @('LICENSE.txt','ThirdPartyNotices.txt')) {
        Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $dotnetExe) $name) -Destination (Join-Path $payload "DOTNET-$name")
    }
    Get-ChildItem -LiteralPath $payload -File | Get-FileHash -Algorithm SHA256 | ForEach-Object { $_.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_.Path) } | Set-Content -LiteralPath (Join-Path $payload 'SHA256SUMS.txt') -Encoding UTF8
    New-Item -ItemType Directory -Path $output | Out-Null
    Get-ChildItem -LiteralPath $payload | ForEach-Object { Move-Item -LiteralPath $_.FullName -Destination $output }
    $files = @(Get-ChildItem -LiteralPath $output -Recurse -File)
    $result = [ordered]@{
        output=$output
        executableBytes=(Get-Item -LiteralPath (Join-Path $output 'ClassroomCaption.exe')).Length
        totalBytes=($files | Measure-Object Length -Sum).Sum
        fileCount=$files.Count
        selfContained=$true
        persistentProgramCache=$true
        embeddedSavedConfiguration=[bool]$IncludeSavedConfiguration
        standaloneVerificationPassed=$true
        verificationMilliseconds=[math]::Round($watch.Elapsed.TotalMilliseconds)
        screenOrMicrophoneCaptured=$false
        modelCalls=$false
    }
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'publication-report.json') -Encoding UTF8
    $result | ConvertTo-Json
} finally {
    # Only exact temporary files created by this invocation; original user data stays put.
    foreach ($temporary in @($credentialInput,$settingsInput,$verificationCredentials,$standaloneExe,(Join-Path $stage 'wrapped\ClassroomCaption.exe'))) {
        $resolved = [IO.Path]::GetFullPath($temporary)
        if (-not $resolved.StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected temporary path.' }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Force }
    }
    $verificationCache=[IO.Path]::GetFullPath((Join-Path $verifyData 'program-cache'))
    if(-not $verificationCache.StartsWith($stage+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected verification cache path.'}
    if(Test-Path -LiteralPath $verificationCache){
        if((Get-Item -LiteralPath $verificationCache).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Verification cache is a linked directory.'}
        if(Get-ChildItem -LiteralPath $verificationCache -Force -Recurse|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}){throw 'Verification cache contains a linked path.'}
        Remove-Item -LiteralPath $verificationCache -Recurse -Force
    }
}
