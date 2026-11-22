# Rebuilds eq/presets.txt from the oratory1990 results in AutoEq, pinned to one commit.
# Needs network access and curl.exe (ships with Windows 10 1803 and later). Run: .\eq\Build-Presets.ps1
param([string]$Out = (Join-Path $PSScriptRoot 'presets.txt'))
$ErrorActionPreference = 'Stop'
$commit = '7ae0f56d53074872b028649617a22bbb4232feb7'
$root = 'results/oratory1990/'
$agent = 'DeskAudio-preset-builder'
$filter = '^Filter \d+: ON (PK|LSC|HSC) Fc \d+(\.\d+)? Hz Gain -?\d+(\.\d+)? dB Q \d+(\.\d+)?$'
$lf = [string][char]10
$cr = [string][char]13
$utf8 = New-Object Text.UTF8Encoding $false
$curl = (Get-Command curl.exe -CommandType Application | Select-Object -First 1).Source
if (-not $curl) { throw 'curl.exe was not found.' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('DeskAudio.Presets.' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$items = New-Object 'Collections.Generic.List[object]'
$skipped = New-Object 'Collections.Generic.List[string]'
$seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
try {
    foreach ($category in 'over-ear', 'in-ear') {
        # Native output is decoded in the OEM code page by Windows PowerShell 5.1, so read the listing from a file.
        $listFile = Join-Path $work ($category + '.json')
        $api = 'https://api.github.com/repos/jaakkopasanen/AutoEq/contents/{0}{1}?ref={2}' -f $root, $category, $commit
        & $curl -sS --fail -m 60 -A $agent -H 'Accept: application/vnd.github+json' -o $listFile $api
        if ($LASTEXITCODE -ne 0) { throw ('Could not list ' + $category) }
        $listing = ConvertFrom-Json ([IO.File]::ReadAllText($listFile, $utf8))
        $names = @(foreach ($entry in $listing) { if ($entry.type -eq 'dir') { $entry.name } })
        $jobs = New-Object 'Collections.Generic.List[object]'
        $config = New-Object Text.StringBuilder
        foreach ($name in $names) {
            $label = $category + '/' + $name
            if ($name -match '[\[\]]') { $skipped.Add($label + ' (brackets in name)'); continue }
            if (-not $seen.Add($name)) { $skipped.Add($label + ' (duplicate name)'); continue }
            $part = [Uri]::EscapeDataString($name)
            $file = Join-Path $work ($category + '-' + $jobs.Count + '.txt')
            $url = 'https://raw.githubusercontent.com/jaakkopasanen/AutoEq/{0}/{1}{2}/{3}/{3}%20ParametricEQ.txt' -f $commit, $root, $category, $part
            [void]$config.Append('url = "' + $url + '"' + $lf + 'output = "' + $file.Replace('\', '/') + '"' + $lf)
            $jobs.Add([pscustomobject]@{ Name = $name; File = $file; Label = $label })
        }
        $configFile = Join-Path $work ($category + '.cfg')
        [IO.File]::WriteAllText($configFile, $config.ToString(), (New-Object Text.ASCIIEncoding))
        # One curl process fetches every file. A failed transfer leaves no output file and is reported below.
        & $curl -sS --fail --parallel --parallel-max 6 --retry 3 -m 60 -A $agent --config $configFile
        foreach ($job in $jobs) {
            if (-not (Test-Path -LiteralPath $job.File)) { $skipped.Add($job.Label + ' (download failed)'); continue }
            $text = [IO.File]::ReadAllText($job.File, $utf8)
            $rows = @(($text.Replace($cr, '') -split $lf) | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            $bad = $rows.Count -ne 11 -or $rows[0] -notmatch '^Preamp: -?\d+(\.\d+)? dB$' -or @($rows[1..10] | Where-Object { $_ -notmatch $filter }).Count -gt 0
            if ($bad) { $skipped.Add($job.Label + ' (unexpected layout)'); continue }
            $items.Add([pscustomobject]@{ Name = $job.Name; Category = $category; Rows = $rows })
        }
    }
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
$items.Sort([Comparison[object]]{ param($a, $b) [string]::Compare($a.Name, $b.Name, [StringComparison]::OrdinalIgnoreCase) })
$sections = foreach ($item in $items) {
    $head = '[' + $item.Name + ']'
    if ($item.Category -ne 'over-ear') { $head += $lf + 'Category: ' + $item.Category }
    $head + $lf + ($item.Rows -join $lf)
}
[IO.File]::WriteAllText($Out, (($sections -join ($lf + $lf)) + $lf), $utf8)
foreach ($category in 'over-ear', 'in-ear') { $category + ': ' + @($items | Where-Object { $_.Category -eq $category }).Count }
if ($skipped.Count -gt 0) { 'Skipped ' + $skipped.Count; $skipped }
