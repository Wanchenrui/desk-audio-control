$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$dir = Join-Path ([IO.Path]::GetTempPath()) ('DeskAudio.UpdateTest.' + [guid]::NewGuid().ToString('N'))
$prior = $env:DESKAUDIO_UPDATE_TEST
$fixture = Join-Path $PSScriptRoot 'UpdateProcessTest.cs'
$shared = Join-Path $project 'UpdatePackage.cs'
$client = 'SettingsFile.cs','UpdateCheck.cs','UpdateInstall.cs' | ForEach-Object { Join-Path $project $_ }
try {
    [void][IO.Directory]::CreateDirectory($dir)
    $helper = Join-Path $dir 'helper.exe'
    & $compiler /nologo /platform:x64 /codepage:65001 /r:System.Windows.Forms.dll /main:UpdateProcessTest /define:TEST_HELPER "/out:$helper" $shared (Join-Path $project 'updater\Updater.cs') $fixture
    if ($LASTEXITCODE) { throw 'Helper test build failed' }
    foreach ($version in 'old','new') {
        $bin = [IO.Directory]::CreateDirectory((Join-Path $dir $version)).FullName
        $options = @()
        if ($version -eq 'new') { $options += '/define:NEXT_RELEASE' }
        & $compiler /nologo /platform:x64 /codepage:65001 /r:System.Web.Extensions.dll /r:System.Security.dll /main:UpdateProcessTest @options "/resource:$helper,DeskAudio.Updater.exe" "/out:$bin\DeskAudio.exe" $shared @client $fixture
        if ($LASTEXITCODE) { throw "Client test build failed ($version)" }
    }
    foreach ($mode in 'install','rollback','corrupt') {
        $base = [IO.Directory]::CreateDirectory((Join-Path $dir ("安装 with spaces " + $mode))).FullName
        $env:DESKAUDIO_UPDATE_TEST = $base
        $incoming = [IO.Directory]::CreateDirectory((Join-Path $base 'incoming')).FullName
        $target = Join-Path $base 'DeskAudio.exe'
        Copy-Item -LiteralPath (Join-Path $dir 'old\DeskAudio.exe') -Destination $target
        Copy-Item -LiteralPath (Join-Path $dir 'new\DeskAudio.exe') -Destination $incoming
        if ($mode -eq 'rollback') { [IO.File]::WriteAllText((Join-Path $base 'fail-boot'),'1') }
        & $target $mode
        if ($LASTEXITCODE) { throw "Handoff failed ($mode)" }
        if ($mode -ne 'corrupt') {
            $marker = if ($mode -eq 'install') {'new-started'} else {'restored'}
            $timeout = [Diagnostics.Stopwatch]::StartNew()
            while (!(Test-Path -LiteralPath (Join-Path $base $marker)) -or !(Test-Path -LiteralPath (Join-Path $base 'helper-finished'))) {
                if ($timeout.Elapsed.TotalSeconds -gt 40) { throw "Timed out ($mode)" }
                Start-Sleep -Milliseconds 50
            }
        }
        $expected = if ($mode -eq 'install') {'new\DeskAudio.exe'} else {'old\DeskAudio.exe'}
        if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath (Join-Path $dir $expected)).Hash) { throw "Installed bytes differ ($mode)" }
        if (Get-ChildItem -LiteralPath $base -Filter '.DeskAudio-*') { throw "Unconsumed update files ($mode)" }
        Write-Output "PASS updater process $mode"
    }
} finally {
    $env:DESKAUDIO_UPDATE_TEST = $prior
    $root = [IO.Path]::GetFullPath($dir)
    if ([IO.Path]::GetDirectoryName($root).TrimEnd('\') -eq [IO.Path]::GetTempPath().TrimEnd('\') -and
        [IO.Path]::GetFileName($root) -match '^DeskAudio\.UpdateTest\.[a-f0-9]{32}$' -and (Test-Path -LiteralPath $root)) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
