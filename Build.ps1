$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = 'AssemblyInfo.cs', 'RouteFeedback.cs', 'ApoBridge.cs', 'EqPlanner.cs', 'EqMatcher.cs', 'MicAdvisor.cs', 'MicSpectrum.cs', 'LevelPlanner.cs', 'BluetoothLink.cs', 'DevicePresence.cs', 'SwitchGuard.cs', 'Theme.cs', 'UiBasics.cs', 'UiButton.cs', 'UiToggle.cs', 'UiSlider.cs', 'UiCard.cs', 'UiList.cs', 'UiShell.cs', 'UiMenu.cs', 'SmartDesk.cs', 'AudioPanel.cs', 'AudioPanel.Logic.cs', 'AudioPanel.Actions.cs', 'AudioPanel.Devices.cs', 'AudioPanel.Pages.cs', 'EndpointEvents.cs', 'EndpointCache.cs', 'ScanGate.cs', 'AudioEventBus.cs', 'PanelAudio.cs', 'SceneSwitch.cs', 'SceneContext.cs', 'AudioPanel.Smart.cs', 'RouteTransaction.cs', 'SettingsFile.cs', 'DecisionJournal.cs', 'AudioPanel.Decisions.cs', 'RouteOverlay.cs', 'AudioCore.cs', 'AudioDiscovery.cs', 'SmartPolicy.cs', 'AudioTuning.cs', 'AudioPanel.Landing.cs', 'AudioPanel.TuningPage.cs', 'TuningStream.cs', 'MicDoctor.cs', 'VolumeTuning.cs', 'EqPreset.cs', 'ModelPicker.cs', 'EqTable.cs', 'QuickPanel.cs', 'UpdateCheck.cs', 'UpdatePage.cs'
Push-Location -LiteralPath $PSScriptRoot
$helper = Join-Path ([IO.Path]::GetTempPath()) ('DeskAudio.Updater.' + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /r:System.Windows.Forms.dll /win32manifest:DeskAudio.manifest "/out:$helper" UpdatePackage.cs (Join-Path $PSScriptRoot 'updater\Updater.cs')
    if ($LASTEXITCODE -ne 0) { throw 'DeskAudio updater compilation failed.' }
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Accessibility.dll /r:System.Security.dll /r:System.Web.Extensions.dll /resource:eq\presets.txt,DeskAudio.EQ.txt /resource:eq\name-aliases.txt,DeskAudio.EQ.Aliases /resource:eq\AutoEq-LICENSE.txt,DeskAudio.EQ.License /win32icon:DeskAudio.ico /win32manifest:DeskAudio.manifest "/resource:$helper,DeskAudio.Updater.exe" /out:DeskAudio.exe $sources UpdatePackage.cs UpdateInstall.cs
    if ($LASTEXITCODE -ne 0) { throw 'DeskAudio compilation failed.' }
} finally {
    if (Test-Path -LiteralPath $helper) { Remove-Item -LiteralPath $helper }
    Pop-Location
}
