param([string[]]$TargetDirectories = @('dist\俄语课堂字幕', 'dist\俄语课堂字幕-1.1'))
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceDirectory = Join-Path $projectRoot 'artifacts\development\Release\net10.0-windows'
$names = @('ClassroomCaption.dll','ClassroomCaption.Core.dll','rnnoise.dll','RNNoise-COPYING.txt','THIRD-PARTY-NOTICES.txt')
foreach ($name in $names) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory $name))) { throw 'Build the development version before applying this local update.' }
}
$targets = @()
foreach ($directory in $TargetDirectories) {
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $projectRoot $directory))
    if (-not $candidate.StartsWith($projectRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Target is outside this workspace.' }
    if (-not (Test-Path -LiteralPath (Join-Path $candidate 'ClassroomCaption.dll'))) { continue }
    $runtime = Get-Content -Raw -LiteralPath (Join-Path $candidate 'ClassroomCaption.runtimeconfig.json') | ConvertFrom-Json
    if ($runtime.runtimeOptions.tfm -ne 'net10.0') { throw 'Target runtime is not compatible with this update.' }
    $targets += $candidate
}
if ($targets.Count -eq 0) { throw 'No existing application installation was found.' }
$processes = @(Get-CimInstance Win32_Process -Filter "Name='ClassroomCaption.exe' OR Name='dotnet.exe'")
foreach ($target in $targets) {
    $executable = Join-Path $target 'ClassroomCaption.exe'
    $assembly = Join-Path $target 'ClassroomCaption.dll'
    $running = @($processes | Where-Object { $_.ExecutablePath -eq $executable -or ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -match [regex]::Escape($assembly)) })
    if ($running.Count -gt 0) { throw 'Please close the running classroom application before updating; it has not been stopped automatically.' }
}
$backupRoot = Join-Path $projectRoot ('artifacts\local-update-backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,6))
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
$updated = @()
$report = @()
try {
    for ($i = 0; $i -lt $targets.Count; $i++) {
        $target = $targets[$i]
        $backup = Join-Path $backupRoot $i.ToString()
        New-Item -ItemType Directory -Path $backup -Force | Out-Null
        foreach ($name in $names) {
            $source = Join-Path $sourceDirectory $name
            $destination = Join-Path $target $name
            $expectedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash
            $exists = Test-Path -LiteralPath $destination
            if ($exists -and (Get-FileHash -Algorithm SHA256 -LiteralPath $destination).Hash -eq $expectedHash) { continue }
            $saved = $null
            if ($exists) { $saved = Join-Path $backup $name; Copy-Item -LiteralPath $destination -Destination $saved }
            $updated += [pscustomobject]@{Destination=$destination;Backup=$saved}
            Copy-Item -LiteralPath $source -Destination $destination -Force
            if ((Get-FileHash -Algorithm SHA256 -LiteralPath $destination).Hash -ne $expectedHash) { throw 'Updated assembly verification failed.' }
            $report += [pscustomobject]@{target=$destination;sha256=$expectedHash.ToLowerInvariant()}
        }
    }
} catch {
    foreach ($entry in $updated) {
        if ($entry.Backup) { Copy-Item -LiteralPath $entry.Backup -Destination $entry.Destination -Force }
        else { Remove-Item -LiteralPath $entry.Destination -Force -ErrorAction SilentlyContinue }
    }
    throw
}
[pscustomobject]@{updatedFiles=$report;portablePackageCreated=$false;settingsTouched=$false} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backupRoot 'update-report.json') -Encoding UTF8
Write-Output ([pscustomobject]@{updatedFiles=$report.Count;installations=$targets.Count;settingsTouched=$false;portablePackageCreated=$false} | ConvertTo-Json -Compress)
