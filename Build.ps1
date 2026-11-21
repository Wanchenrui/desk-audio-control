$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = 'AssemblyInfo.cs', 'SmartDesk.cs', 'EndpointEvents.cs', 'AudioCore.cs', 'AudioDiscovery.cs', 'SmartPolicy.cs', 'AudioTuning.cs', 'UpdateCheck.cs', 'UpdatePage.cs'
Push-Location -LiteralPath $PSScriptRoot
try {
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Accessibility.dll /r:System.Security.dll /r:System.Web.Extensions.dll /win32icon:DeskAudio.ico /win32manifest:DeskAudio.manifest /out:DeskAudio.exe $sources
    if ($LASTEXITCODE -ne 0) { throw 'DeskAudio compilation failed.' }
} finally {
    Pop-Location
}
