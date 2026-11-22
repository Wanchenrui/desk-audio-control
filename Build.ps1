$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = 'AssemblyInfo.cs', 'Theme.cs', 'UiBasics.cs', 'UiButton.cs', 'UiToggle.cs', 'UiSlider.cs', 'UiCard.cs', 'UiList.cs', 'UiShell.cs', 'UiMenu.cs', 'SmartDesk.cs', 'AudioPanel.cs', 'AudioPanel.Logic.cs', 'AudioPanel.Actions.cs', 'AudioPanel.Devices.cs', 'AudioPanel.Pages.cs', 'EndpointEvents.cs', 'EndpointCache.cs', 'ScanGate.cs', 'AudioEventBus.cs', 'PanelAudio.cs', 'RouteTransaction.cs', 'SettingsFile.cs', 'DecisionJournal.cs', 'AudioPanel.Decisions.cs', 'RouteOverlay.cs', 'AudioCore.cs', 'AudioDiscovery.cs', 'SmartPolicy.cs', 'AudioTuning.cs', 'UpdateCheck.cs', 'UpdatePage.cs'
Push-Location -LiteralPath $PSScriptRoot
try {
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Accessibility.dll /r:System.Security.dll /r:System.Web.Extensions.dll /win32icon:DeskAudio.ico /win32manifest:DeskAudio.manifest /out:DeskAudio.exe $sources
    if ($LASTEXITCODE -ne 0) { throw 'DeskAudio compilation failed.' }
} finally {
    Pop-Location
}
