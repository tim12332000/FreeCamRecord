param([string]$Executable, [string]$OutputDirectory, [int]$Fps = 0, [int]$Seconds = 8)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class ActualUi {
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out R r);
 [DllImport("user32.dll")] public static extern bool EnumWindows(Callback callback,IntPtr p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h,StringBuilder s,int size);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 public delegate bool Callback(IntPtr h,IntPtr p);
 public struct R {public int Left,Top,Right,Bottom;}
}
'@
[void][ActualUi]::SetThreadDpiAwarenessContext([IntPtr](-4))
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$trace = New-Object 'System.Collections.Generic.List[string]'
$app = Start-Process -FilePath $Executable -PassThru
function Controls {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,[int]$app.Id)
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$condition)
    foreach ($window in $windows) { $window; $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) }
}
function Find-Control([string]$name) {
    $limit = [Diagnostics.Stopwatch]::StartNew()
    do {
        $control = Controls | Where-Object { $_.Current.Name -match $name -and -not $_.Current.IsOffscreen } | Select-Object -First 1
        if ($control) { return $control }
        Start-Sleep -Milliseconds 80
    } while ($limit.Elapsed.TotalSeconds -lt 12)
    throw "UI control missing: $name"
}
function Click($control) {
    $rect = $control.Current.BoundingRectangle
    [void][ActualUi]::SetCursorPos([int]($rect.X+$rect.Width/2),[int]($rect.Y+$rect.Height/2))
    Start-Sleep -Milliseconds 50
    [ActualUi]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [ActualUi]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 120
}
function Drag([int]$x,[int]$y,[int]$toX,[int]$toY) {
    [void][ActualUi]::SetCursorPos($x,$y)
    [ActualUi]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 120
    for ($i=1;$i -le 16;$i++) {
        [void][ActualUi]::SetCursorPos([int]($x+($toX-$x)*$i/16),[int]($y+($toY-$y)*$i/16))
        Start-Sleep -Milliseconds 25
    }
    [ActualUi]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 250
}
try {
    $null = Find-Control '^開始錄影$'
    $browserWindow = [IntPtr]::Zero
    $callback = [ActualUi+Callback]{ param($handle,$parameter)
        $title = New-Object Text.StringBuilder 512
        [void][ActualUi]::GetWindowText($handle,$title,512)
        if ($title.ToString().StartsWith('FreeCamRecord Motion Bench')) { $script:browserWindow=$handle;return $false }
        return $true
    }
    [void][ActualUi]::EnumWindows($callback,[IntPtr]::Zero)
    $browserRect = New-Object ActualUi+R
    if ($browserWindow -eq [IntPtr]::Zero -or -not [ActualUi]::GetWindowRect($browserWindow,[ref]$browserRect)) { throw 'Chrome test window not found' }
    [uint32]$browserProcessId = 0
    [void][ActualUi]::GetWindowThreadProcessId($browserWindow,[ref]$browserProcessId)
    $browserProcess = Get-Process -Id $browserProcessId
    $trace.Add("Actual browser: $($browserProcess.Path), version=$((Get-Item $browserProcess.Path).VersionInfo.FileVersion)")
    [void][ActualUi]::SetCursorPos(($browserRect.Left+100),($browserRect.Top+400))
    [ActualUi]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [ActualUi]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 150
    $app.Refresh()
    $frame = New-Object ActualUi+R
    [void][ActualUi]::GetWindowRect($app.MainWindowHandle,[ref]$frame)
    $desiredLeft=$browserRect.Left-4; $desiredTop=$browserRect.Top-36
    Drag ($frame.Left+220) ($frame.Top+15) ($desiredLeft+220) ($desiredTop+15)
    [void][ActualUi]::GetWindowRect($app.MainWindowHandle,[ref]$frame)
    Drag ($frame.Right-2) ($frame.Bottom-2) ($browserRect.Right+2) ($browserRect.Bottom+2)
    [void][ActualUi]::GetWindowRect($app.MainWindowHandle,[ref]$frame)
    $trace.Add("Physical drag/resize: frame=$($frame.Left),$($frame.Top),$($frame.Right),$($frame.Bottom)")
    if ($frame.Left -ne $desiredLeft -or $frame.Top -ne $desiredTop -or $frame.Right -ne $browserRect.Right+4 -or $frame.Bottom -ne $browserRect.Bottom+4) { throw 'Physical drag/resize did not select the requested Chrome window' }
    Click (Find-Control '^設定選單$')
    $audio = Find-Control '^系統音效$'
    $settingsPath = "$env:LOCALAPPDATA\FreeCamRecord\settings.json"
    $beforeAudio = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($beforeAudio.SystemAudio) { Click $audio; Click (Find-Control '^設定選單$'); $audio=Find-Control '^系統音效$' }
    Click $audio
    $afterAudio = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $afterAudio.SystemAudio) { throw 'System audio is not enabled' }
    $trace.Add('Enabled system audio by actual menu clicks and verified the setting saved by the application')
    Click (Find-Control '^設定選單$')
    Click (Find-Control '^幀率：')
    if ($Fps -eq 0) { $choice=Find-Control '^自動：'; $actualFps=[int]([regex]::Match($choice.Current.Name,'(\d+) Hz').Groups[1].Value); Click $choice } else { $actualFps=$Fps; Click (Find-Control "^$Fps FPS`$") }
    $preferences = Get-Content -LiteralPath "$env:LOCALAPPDATA\FreeCamRecord\settings.json" -Raw -Encoding UTF8 | ConvertFrom-Json
    $existing = @(Get-ChildItem -LiteralPath $preferences.Directory -Filter *.mp4 | Select-Object -ExpandProperty FullName)
    Click (Find-Control '^開始錄影$')
    $null = Find-Control '^停止錄影$'
    $trace.Add('Actual start button clicked and recording state observed')
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds $Seconds
    Click (Find-Control '^停止錄影$')
    $null = Find-Control '^開始錄影$'
    $clock.Stop()
    $trace.Add('Actual stop button clicked and idle state observed')
    $clip = Get-ChildItem -LiteralPath $preferences.Directory -Filter *.mp4 | Where-Object { $_.FullName -notin $existing } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $clip) { throw 'The UI did not produce an MP4 file' }
    $fpsLabel = $actualFps
    $report = @{Path=$clip.FullName; Error=$null; Seconds=$clock.Elapsed.TotalSeconds; CaptureRate=$null; Fps=$fpsLabel; Area=@{X=$frame.Left+4;Y=$frame.Top+36;Width=$frame.Right-$frame.Left-8;Height=$frame.Bottom-$frame.Top-40}; SystemAudio=$preferences.SystemAudio;Executable=(Get-Item $Executable).FullName;Hash=(Get-FileHash -LiteralPath $Executable).Hash;Version=(Get-Item $Executable).VersionInfo.FileVersion;BrowserExecutable=$browserProcess.Path;BrowserVersion=(Get-Item $browserProcess.Path).VersionInfo.FileVersion}
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'recording.json') -Encoding UTF8
    $trace | Set-Content -LiteralPath (Join-Path $OutputDirectory 'ui-trace.txt') -Encoding UTF8
    $report | ConvertTo-Json -Compress -Depth 6
} catch {
    $trace.Add($_.Exception.ToString())
    $trace | Set-Content -LiteralPath (Join-Path $OutputDirectory 'ui-trace.txt') -Encoding UTF8
    throw
} finally {
    if (-not $app.HasExited) { [void]$app.CloseMainWindow(); [void]$app.WaitForExit(15000) }
}
