param(
    [Parameter(Mandatory = $true)][string]$VerificationDirectory,
    [Parameter(Mandatory = $true)][string]$FfmpegPath
)

# Development verification only. The application never needs FFmpeg.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$verifyPath = (Resolve-Path -LiteralPath $VerificationDirectory).Path
$ffmpegExe = (Resolve-Path -LiteralPath $FfmpegPath).Path
$report = New-Object 'System.Collections.Generic.List[string]'

function Invoke-Decoder([string[]]$ToolArguments) {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $ffmpegExe
    # Windows filenames cannot contain quotes; values are passed directly to CreateProcess, without a shell.
    $startInfo.Arguments = ($ToolArguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.RedirectStandardOutput = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        [void]$process.Start()
        $errors = $process.StandardError.ReadToEndAsync()
        $output = $process.StandardOutput.ReadToEndAsync()
        $process.WaitForExit()
        $log = $errors.GetAwaiter().GetResult() + $output.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw $log }
        return $log
    } finally { $process.Dispose() }
}

$lines = Get-Content -LiteralPath (Join-Path $verifyPath 'results.txt') -Encoding UTF8
if ($lines -notcontains 'PASS: complete') { throw 'The desktop self-test did not complete.' }
$mp4Count = 0
foreach ($line in $lines) {
    $match = [regex]::Match($line, '^MP4: (.+?); expectedFps=(\d+); width=(\d+); height=(\d+)')
    if (-not $match.Success) { continue }
    $clip = $match.Groups[1].Value
    $fps = [int]$match.Groups[2].Value
    $width = [int]$match.Groups[3].Value
    $height = [int]$match.Groups[4].Value
    $log = Invoke-Decoder @('-hide_banner', '-i', $clip, '-f', 'null', '-')
    if ($log -notmatch 'Video: h264') { throw "Expected H.264: $clip" }
    $rate = [regex]::Match($log, 'Video:[^\r\n]*?([0-9.]+) fps')
    # Clock-aligned skipped slots legitimately reduce average FPS without shortening the timeline.
    $averageFps = if ($rate.Success) { [double]::Parse($rate.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture) } else { 0 }
    if ($averageFps -le 0 -or $averageFps -gt $fps + 0.02) { throw "Invalid MP4 frame rate for target ${fps}: $clip" }
    if ($log -notmatch ([regex]::Escape("${width}x${height}"))) { throw "Wrong MP4 dimensions: $clip" }
    $preview = $clip + '.decoded.bmp'
    [void](Invoke-Decoder @('-y', '-v', 'error', '-i', $clip, '-frames:v', '1', $preview))
    $image = [Drawing.Bitmap]::FromFile($preview)
    try {
        $top = $image.GetPixel(10, 10)
        $bottom = $image.GetPixel($width - 10, $height - 10)
        if ($top.R -lt 180 -or $top.G -gt 85 -or $top.B -gt 85) { throw "Top-left crop/orientation failed: $clip" }
        if ($bottom.R -lt 180 -or $bottom.G -lt 180 -or $bottom.B -gt 85) { throw "Bottom-right crop/orientation failed: $clip" }
        $pad = $image.GetPixel($width - 1, [int]($height / 2))
        if ($pad.R -gt 45 -or $pad.G -gt 45 -or $pad.B -gt 45) { throw "Expected one black padding column: $clip" }
    } finally { $image.Dispose() }
    if ($line.Contains('audio=AAC')) {
        if ($log -notmatch 'Audio: aac') { throw "AAC stream missing: $clip" }
        $pcm = $clip + '.decoded.pcm'
        [void](Invoke-Decoder @('-y', '-v', 'error', '-i', $clip, '-map', '0:a:0', '-c:a', 'pcm_s16le', '-f', 's16le', $pcm))
        $sound = [IO.File]::ReadAllBytes($pcm)
        if ($sound.Length -lt 192000 -or -not ($sound | Where-Object { $_ -ne 0 } | Select-Object -First 1)) { throw "AAC audio is empty or silent: $clip" }
    }
    $report.Add("PASS: MP4 H264 ${width}x${height} ${fps}fps; exact crop, padding and full decode: $([IO.Path]::GetFileName($clip))")
    $mp4Count++
}
if ($mp4Count -lt 4) { throw 'Expected 30/120/automatic/144 FPS MP4 recordings.' }
foreach ($clip in Get-ChildItem -LiteralPath $verifyPath -Filter '*.avi') {
    $log = Invoke-Decoder @('-hide_banner', '-i', $clip.FullName, '-f', 'null', '-')
    if ($log -notmatch 'Video: mjpeg') { throw "Expected MJPEG: $($clip.Name)" }
    $report.Add("PASS: AVI full decode: $($clip.Name)")
}
$report | Set-Content -LiteralPath (Join-Path $verifyPath 'decoder-results.txt') -Encoding UTF8
$report
