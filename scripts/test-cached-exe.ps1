param(
 [Parameter(Mandatory=$true)][string]$Executable,
 [Parameter(Mandatory=$true)][string]$InnerExe
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$exe=[IO.Path]::GetFullPath($Executable)
$inner=[IO.Path]::GetFullPath($InnerExe)
$work=Join-Path $root ('artifacts\cache-package-tests\'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work|Out-Null
$results=@()
$hash=(Get-FileHash -LiteralPath $inner -Algorithm SHA256).Hash.ToLowerInvariant()
$cache=Join-Path $work 'cache with spaces'
function Start-Check([string]$name,[string]$cachePath,[string[]]$forward=@(),[string]$program=$exe){
 $report=Join-Path $work ($name+'.json')
 $arguments=@('--program-cache-dir',('"'+$cachePath+'"'),'--bootstrap-report',('"'+$report+'"'),'--bootstrap-no-dialog')+$forward
 $process=Start-Process -FilePath $program -ArgumentList $arguments -WindowStyle Hidden -PassThru
 return [pscustomobject]@{process=$process;report=$report}
}
function Finish-Check($run,[bool]$success=$true){
 if(-not $run.process.WaitForExit(30000)){$run.process.Kill();throw 'Owned bootstrap test timed out.'}
 if(($run.process.ExitCode -eq 0)-ne $success){throw ('Unexpected bootstrap exit: '+$run.process.ExitCode)}
 if(-not(Test-Path -LiteralPath $run.report)){throw 'Bootstrap did not write its diagnostic receipt.'}
 return Get-Content -LiteralPath $run.report -Raw -Encoding UTF8|ConvertFrom-Json
}
function Pass([string]$name){$script:results+=[pscustomobject]@{name=$name;passed=$true};Write-Output ('PASS '+$name)}
try{
 $first=Finish-Check (Start-Check 'first' $cache @('--bootstrap-extract-only'))
 if($first.reused -or -not(Test-Path -LiteralPath $first.cacheExe)-or (Get-FileHash -LiteralPath $first.cacheExe).Hash.ToLowerInvariant() -ne $hash){throw 'First extraction was not exact.'}
 Pass 'First extraction reproduces every byte of the self-contained program'
 $stamp=(Get-Item -LiteralPath $first.cacheExe).LastWriteTimeUtc
 $second=Finish-Check (Start-Check 'reuse' $cache @('--bootstrap-extract-only'))
 if(-not $second.reused -or $second.extractionMs -ne 0 -or (Get-Item -LiteralPath $second.cacheExe).LastWriteTimeUtc -ne $stamp){throw 'Cache was rewritten on a later process launch.'}
 Pass 'A separate later process reuses the cache without writing the EXE again'
 $file=[IO.File]::Open($first.cacheExe,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 try{$file.Position=1024;$byte=$file.ReadByte();$file.Position=1024;$file.WriteByte([byte]($byte -bxor 1))}finally{$file.Dispose()}
 $repaired=Finish-Check (Start-Check 'repair' $cache @('--bootstrap-extract-only'))
 if($repaired.reused -or -not $repaired.repaired -or (Get-FileHash -LiteralPath $first.cacheExe).Hash.ToLowerInvariant() -ne $hash){throw 'A corrupt cached EXE was not repaired.'}
 if(Get-ChildItem -LiteralPath (Split-Path -Parent $first.cacheExe) -Filter '*.tmp' -Force){throw 'Extraction left an unfinished temporary file.'}
 Pass 'Corrupt cache is repaired atomically and no unfinished EXE is left behind'
 $parallelCache=Join-Path $work 'parallel-cache'
 $a=Start-Check 'parallel-a' $parallelCache @('--bootstrap-extract-only')
 $b=Start-Check 'parallel-b' $parallelCache @('--bootstrap-extract-only')
 $ra=Finish-Check $a;$rb=Finish-Check $b
 if(([int]$ra.reused+[int]$rb.reused)-ne 1 -or $ra.cacheExe -ne $rb.cacheExe){throw 'Concurrent extraction was not serialized.'}
 Pass 'Simultaneous launches share one extraction and one version directory'
 $appReport=Join-Path $work 'application.json'
 $data=Join-Path $work 'separate-data'
 $handoff=Finish-Check (Start-Check 'handoff' $cache @('--data-dir',('"'+$data+'"'),'--verify-install',('"'+$appReport+'"')))
 $app=Get-Content -LiteralPath $appReport -Raw -Encoding UTF8|ConvertFrom-Json
 if(-not $handoff.childStarted -or -not $app.passed -or $app.microphoneCaptured -or $app.networkRequests){throw 'Actual self-contained child verification failed.'}
 if($app.launchDirectory -ne (Split-Path -Parent $exe) -or $app.bundleExtractDirectory -ne (Join-Path (Split-Path -Parent $handoff.cacheExe) '原生缓存')){throw 'Original launcher location or persistent native cache was lost.'}
 Pass 'Chinese/spaced paths and arguments reach the real WPF app, font, formula, RNNoise and probe'
 $native=Get-ChildItem -LiteralPath $app.bundleExtractDirectory -Recurse -File -Filter 'rnnoise.dll'|Select-Object -First 1
 if(-not $native){throw 'Native cache fixture missing.'}
 [IO.File]::WriteAllText($native.FullName,'corrupt native cache')
 $nativeReport=Join-Path $work 'native-repair-application.json'
 $nativeRepair=Finish-Check (Start-Check 'native-repair' $cache @('--data-dir',('"'+$data+'"'),'--verify-install',('"'+$nativeReport+'"')))
 if(-not $nativeRepair.nativeRepaired -or -not (Get-Content -LiteralPath $nativeReport -Raw -Encoding UTF8|ConvertFrom-Json).passed){throw 'Native dependency cache was not repaired.'}
 Pass 'Corrupt extracted native dependencies are detected before WPF starts and rebuilt'
 $protected=Join-Path $work 'protected-records';New-Item -ItemType Directory -Path $protected|Out-Null
 $sentinel=Join-Path $protected 'classroom-record.txt';[IO.File]::WriteAllText($sentinel,'keep classroom data')
 $link=Join-Path $work 'linked-cache'
 New-Item -ItemType Junction -Path $link -Target $protected|Out-Null
 try{[void](Finish-Check (Start-Check 'linked' $link @('--bootstrap-extract-only')) $false)
     if([IO.File]::ReadAllText($sentinel)-ne 'keep classroom data' -or @(Get-ChildItem -LiteralPath $protected).Count -ne 1){throw 'Linked cache modified unrelated files.'}
 }finally{Remove-Item -LiteralPath $link -Force}
 Pass 'A linked cache directory is rejected without modifying protected records'
 $badExe=Join-Path $work 'invalid-metadata.exe';$bytes=[IO.File]::ReadAllBytes($exe)
 $needle=[Text.Encoding]::ASCII.GetBytes("CCLZMS2`n"+(Get-Item -LiteralPath $inner).Length+"`n"+$hash)
 $index=-1
 Add-Type -TypeDefinition 'public static class CacheTestPattern {public static int Find(byte[] data,byte[] pattern){for(int i=0;i<=data.Length-pattern.Length;i++){if(data[i]!=pattern[0])continue;int j=1;while(j<pattern.Length&&data[i+j]==pattern[j])j++;if(j==pattern.Length)return i;}return -1;}}'
 $index=[CacheTestPattern]::Find($bytes,$needle)
 if($index -lt 0){throw 'Metadata fixture could not locate the resource.'}
 $bytes[$index+$needle.Length-1]=[byte][char]'g';[IO.File]::WriteAllBytes($badExe,$bytes);$bytes=$null
 [void](Finish-Check (Start-Check 'invalid-metadata' (Join-Path $work 'invalid-cache') @('--bootstrap-extract-only') $badExe) $false)
 Pass 'Invalid packaged metadata fails before any child application can run'
 $result=[ordered]@{passed=$true;tests=$results;firstExtractionMs=$first.extractionMs;reuseValidationMs=$second.validationMs;
   executableBytes=(Get-Item -LiteralPath $exe).Length;innerBytes=(Get-Item -LiteralPath $inner).Length;
   actualRebootTested=$false;microphoneCaptured=$false;modelCalls=$false}
 $result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $work 'results.json') -Encoding UTF8
 $result|ConvertTo-Json -Depth 5
}finally{
 $invalidFixture=[IO.Path]::GetFullPath((Join-Path $work 'invalid-metadata.exe'))
 if(-not $invalidFixture.StartsWith($work+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid fixture cleanup path.'}
 if(Test-Path -LiteralPath $invalidFixture){Remove-Item -LiteralPath $invalidFixture -Force}
 # These exact caches were created by this test; never clean a real E/D program cache or user records.
 foreach($relative in @('cache with spaces','parallel-cache','invalid-cache')){
   $path=[IO.Path]::GetFullPath((Join-Path $work $relative))
   if(-not $path.StartsWith($work+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid test cleanup path.'}
   if(Test-Path -LiteralPath $path){
     if((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Test cache became a linked directory.'}
     if(Get-ChildItem -LiteralPath $path -Force -Recurse|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}){throw 'Test cache contains a linked path.'}
     Remove-Item -LiteralPath $path -Recurse -Force
   }
 }
}
