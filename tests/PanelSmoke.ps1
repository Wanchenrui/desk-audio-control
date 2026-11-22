param([string]$Screenshots)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) ("DeskAudio.Smoke." + [guid]::NewGuid().ToString('N'))
$previousFolder = $env:DESKAUDIO_SMOKE_DIR
try {
    [void][IO.Directory]::CreateDirectory($work)
    $sources = foreach ($item in Get-ChildItem -LiteralPath $project -Filter '*.cs') {
        $source = [IO.File]::ReadAllText($item.FullName)
        # Isolate file persistence and hotkey ownership. Keep the shipping scan loop, UI and routing decisions.
        if ($item.Name -eq 'SettingsFile.cs') {
            $source = $source.Replace('Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskAudioControl")',
                'Environment.GetEnvironmentVariable("DESKAUDIO_SMOKE_DIR")')
        }
        if ($item.Name -eq 'SmartDesk.cs') {
            $source = $source.Replace('Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),Name)', 'AppConfig.Folder')
        }
        if ($item.Name -eq 'AudioPanel.cs') {
            $source = $source -replace 'RegisterHotKey\(window, [123], 0x4003, \(uint\)Keys.F[789]\)', 'false'
        }
        $copy = Join-Path $work $item.Name
        [IO.File]::WriteAllText($copy,$source)
        $copy
    }
    $exe = Join-Path $work 'PanelSmoke.exe'
    $compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Accessibility.dll /r:System.Security.dll /r:System.Web.Extensions.dll /main:PanelSmokeTest "/win32manifest:$project\DeskAudio.manifest" "/out:$exe" @sources (Join-Path $PSScriptRoot 'PanelSmokeTest.cs')
    if ($LASTEXITCODE) { throw 'Smoke compilation failed.' }
    foreach ($mode in 'replay','idle','view light 1','view dark 1','view light 1.25','view dark 1.25') {
        if ($mode.StartsWith('view') -and !$Screenshots) { continue }
        $env:DESKAUDIO_SMOKE_DIR = Join-Path $work ($mode.Replace(' ','-'))
        [void][IO.Directory]::CreateDirectory($env:DESKAUDIO_SMOKE_DIR)
        $parts = $mode.Split(' ')
        & $exe @parts
        if ($LASTEXITCODE) { throw "Panel check failed ($mode)." }
        if ($mode.StartsWith('view')) {
            $dest = Join-Path $Screenshots ($mode.Replace(' ','-'))
            [void][IO.Directory]::CreateDirectory($dest)
            Get-ChildItem -LiteralPath $env:DESKAUDIO_SMOKE_DIR -Filter '*.png' | Copy-Item -Destination $dest
        }
    }
} finally {
    $env:DESKAUDIO_SMOKE_DIR = $previousFolder
    $resolved = [IO.Path]::GetFullPath($work)
    if ([IO.Path]::GetDirectoryName($resolved).TrimEnd('\') -eq [IO.Path]::GetTempPath().TrimEnd('\') -and
        [IO.Path]::GetFileName($resolved) -match '^DeskAudio\.Smoke\.[a-f0-9]{32}$' -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
