$ErrorActionPreference = 'Stop'

$project = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @(
    (Join-Path $project 'AudioCore.cs')
    (Join-Path $project 'AudioDiscovery.cs')
    (Join-Path $project 'SmartPolicy.cs')
)
$tempName = 'DeskAudio.Tests.' + [Guid]::NewGuid().ToString('N') + '.exe'
$tempExe = Join-Path ([IO.Path]::GetTempPath()) $tempName

try {
    foreach ($suite in 'DiscoveryTest', 'SmartPolicyTest') {
        $testSource = Join-Path $PSScriptRoot ($suite + '.cs')
        & $compiler /nologo /target:exe /platform:x64 /codepage:65001 "/out:$tempExe" @sources $testSource
        if ($LASTEXITCODE -ne 0) { throw "Could not compile $suite." }

        & $tempExe
        if ($LASTEXITCODE -ne 0) { throw "$suite failed." }
    }
    $updateCode = Join-Path $project 'UpdateCheck.cs'
    $updateTests = Join-Path $PSScriptRoot 'UpdateCheckTest.cs'
    & $compiler /nologo /target:exe /platform:x64 /codepage:65001 /r:System.Web.Extensions.dll /r:System.Security.dll "/out:$tempExe" $updateCode $updateTests
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile UpdateCheckTest.' }
    & $tempExe
    if ($LASTEXITCODE -ne 0) { throw 'UpdateCheckTest failed.' }
} finally {
    if (Test-Path -LiteralPath $tempExe) { Remove-Item -LiteralPath $tempExe }
}
'All DeskAudio test suites passed.'
