$ErrorActionPreference = 'Stop'

$project = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @(
    (Join-Path $project 'RouteFeedback.cs')
    (Join-Path $project 'ApoBridge.cs')
    (Join-Path $project 'EqPlanner.cs')
    (Join-Path $project 'EqMatcher.cs')
    (Join-Path $project 'MicAdvisor.cs')
    (Join-Path $project 'MicSpectrum.cs')
    (Join-Path $project 'TuningStream.cs')
    (Join-Path $project 'LoopbackComparison.cs')
    (Join-Path $project 'LevelPlanner.cs')
    (Join-Path $project 'BluetoothLink.cs')
    (Join-Path $project 'DevicePresence.cs')
    (Join-Path $project 'SwitchGuard.cs')
    (Join-Path $project 'EndpointCache.cs')
    (Join-Path $project 'ScanGate.cs')
    (Join-Path $project 'AudioEventBus.cs')
    (Join-Path $project 'RouteTransaction.cs')
    (Join-Path $project 'SettingsFile.cs')
    (Join-Path $project 'DecisionJournal.cs')
    (Join-Path $project 'AudioCore.cs')
    (Join-Path $project 'AudioDiscovery.cs')
    (Join-Path $project 'SmartPolicy.cs')
    (Join-Path $project 'SceneSwitch.cs')
    (Join-Path $project 'SceneApplications.cs')
    (Join-Path $project 'SceneContext.cs')
    (Join-Path $project 'PanelAudio.cs')
    (Join-Path $project 'MicDoctor.cs')
    (Join-Path $project 'EqPreset.cs')
    (Join-Path $project 'VolumeTuning.cs')
)
$tempName = 'DeskAudio.Tests.' + [Guid]::NewGuid().ToString('N') + '.exe'
$tempExe = Join-Path ([IO.Path]::GetTempPath()) $tempName

try {
    foreach ($suite in 'DiscoveryTest', 'PresenceTest', 'SmartPolicyTest', 'EndpointCacheTest', 'ScanGateTest', 'FoundationTest', 'SceneSwitchTest', 'TuningTest') {
        $testSource = Join-Path $PSScriptRoot ($suite + '.cs')
        & $compiler /nologo /target:exe /platform:x64 /codepage:65001 /r:System.Web.Extensions.dll "/resource:$project\eq\presets.txt,DeskAudio.EQ.txt" "/resource:$project\eq\name-aliases.txt,DeskAudio.EQ.Aliases" "/resource:$project\eq\AutoEq-LICENSE.txt,DeskAudio.EQ.License" "/out:$tempExe" @sources $testSource
        if ($LASTEXITCODE -ne 0) { throw "Could not compile $suite." }

        & $tempExe
        if ($LASTEXITCODE -ne 0) { throw "$suite failed." }
    }
    $updateCode = Join-Path $project 'UpdateCheck.cs'
    $updateTests = Join-Path $PSScriptRoot 'UpdateCheckTest.cs'
    & $compiler /nologo /target:exe /platform:x64 /codepage:65001 /r:System.Web.Extensions.dll /r:System.Security.dll "/out:$tempExe" $updateCode $updateTests (Join-Path $project 'SettingsFile.cs') (Join-Path $project 'UpdatePackage.cs') (Join-Path $project 'UpdateInstall.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile UpdateCheckTest.' }
    & $tempExe
    if ($LASTEXITCODE -ne 0) { throw 'UpdateCheckTest failed.' }
} finally {
    if (Test-Path -LiteralPath $tempExe) { Remove-Item -LiteralPath $tempExe }
}
& (Join-Path $PSScriptRoot 'UpdateProcess.ps1')
'All DeskAudio test suites passed.'
