param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
function Run([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed with exit code $LASTEXITCODE" }
}
foreach ($target in @(@{Name='x64'; Generator='x64'}, @{Name='x86'; Generator='Win32'})) {
    $build = "artifacts/build-$($target.Name)"
    Run -Program cmake -Arguments @('-S', 'native', '-B', $build, '-A', $target.Generator)
    Run -Program cmake -Arguments @('--build', $build, '--config', 'Release', '--parallel')
    if (!$SkipTests) { Run -Program ctest -Arguments @('--test-dir', $build, '-C', 'Release', '--output-on-failure', '--verbose') }
    Run -Program cmake -Arguments @('--install', $build, '--config', 'Release', '--prefix', "artifacts/native/$($target.Name)")
}
Run -Program dotnet -Arguments @('publish', 'src/SmoothFrames/SmoothFrames.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None', '-o', 'artifacts/publish')
Copy-Item LICENSE artifacts/publish/LICENSE.txt
Copy-Item artifacts/native/x64/MinHook-LICENSE.txt artifacts/publish/MinHook-LICENSE.txt
Copy-Item README.md artifacts/publish/README.md
# This executes the embedded helpers extraction from the actual self-contained EXE.
$verify = Start-Process artifacts/publish/SmoothFrames.exe -ArgumentList '--verify-engine' -Wait -PassThru
if ($verify.ExitCode -ne 0) { throw 'Packaged engine verification failed' }
if (!$SkipTests) {
    $ui = Start-Process artifacts/publish/SmoothFrames.exe -ArgumentList '--smoke-ui' -PassThru
    if (!$ui.WaitForExit(15000)) { $ui.Kill(); throw 'UI startup smoke test timed out' }
    if ($ui.ExitCode -ne 0) { throw 'UI startup smoke test failed' }
}
Compress-Archive -Path artifacts/publish/* -DestinationPath artifacts/SmoothFrames-win-x64.zip -Force
