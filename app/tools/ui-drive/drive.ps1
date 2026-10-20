# Step 6a/6b UI run (read-only inspector): opens project C6 (must exist under work\edits\C6), selects MatchTimeBase >
# Img_Plate, MatchTime > score box and library uiImage, and screenshots each. Changes nothing on disk.
# usage: powershell -NoProfile -ExecutionPolicy Bypass -File drive.ps1 -Exe <UmgEditor.App.exe> -Out <folder for PNGs>
param([string]$Exe, [string]$Out)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); }
"@
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

function Find($root, $prop, $value, $timeout = 60) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($prop, $value)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeout) {
        $e = $root.FindFirst($TS::Descendants, $cond)
        if ($e) { return $e }
        Start-Sleep -Milliseconds 300
    }
    throw "not found: $value"
}
function ById($root, $id, $t = 60) { Find $root $AE::AutomationIdProperty $id $t }
function ByName($root, $name, $t = 30) { Find $root $AE::NameProperty $name $t }
function Select-Item($e) { $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Expand($e) { try { $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() } catch {} }
function Shot($win, $name) {
    [W]::SetForegroundWindow([IntPtr]$win.Current.NativeWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 700
    $r = $win.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
    $bmp.Save("$Out\$name.png"); $g.Dispose(); $bmp.Dispose()
    "shot $name"
}
function Open-Package($win, $search, $fileSuffix) {
    $box = ById $win 'SearchBox'
    $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($search)
    Start-Sleep -Milliseconds 900
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)
    $items = $win.FindAll($TS::Descendants, $cond)
    $hit = $items | Where-Object { $_.Current.Name -like "*$fileSuffix*" } | Select-Object -First 1
    if (-not $hit) { $names = ($items | Select-Object -First 5 | ForEach-Object { $_.Current.Name }) -join " || "; throw "package $fileSuffix not in list ($($items.Count) items): $names" }
    Select-Item $hit
    Start-Sleep -Seconds 3
}
function Select-TreePath($win, [string[]]$names) {
    $tree = ById $win 'WidgetTreeView'
    $node = $tree
    foreach ($n in $names) {
        $node = ByName $node $n 20
        if ($n -ne $names[-1]) { Expand $node; Start-Sleep -Milliseconds 400 }
    }
    Select-Item $node
    Start-Sleep -Seconds 4
}

$p = Start-Process $Exe -PassThru
$win = $null
$sw = [Diagnostics.Stopwatch]::StartNew()
while (-not $win -and $sw.Elapsed.TotalSeconds -lt 60) {
    Start-Sleep -Milliseconds 500
    $p.Refresh()
    if ($p.MainWindowHandle -ne 0) { $win = $AE::FromHandle($p.MainWindowHandle) }
}
if (-not $win) { throw "no window" }
try { $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState('Maximized') } catch {}

# wait for the index and the project list
$picker = ById $win 'ProjectPicker' 60
$sw.Restart()
while ($sw.Elapsed.TotalSeconds -lt 120) {
    Expand $picker; Start-Sleep -Milliseconds 400
    $c6 = $picker.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'C6')))
    if ($c6) { break }
    try { $picker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch {}
    Start-Sleep -Seconds 1
}
if (-not $c6) { throw "project C6 not listed" }
Select-Item $c6
Start-Sleep -Seconds 2

# 1) MatchTimeBase > Img_Plate in project C6 (edited Right = 100, 8 embedding packages, online collapsed)
Open-Package $win 'MatchTime/MatchTimeBase' 'MatchTimeBase.uasset'
Select-TreePath $win @('Img_Plate')
Shot $win '1_matchtimebase_plate'

# 2) MatchTime > score box (margin animation Danger warning, edited Left = 146 in C6)
Open-Package $win 'MatchTime/MatchTime' '/MatchTime/MatchTime.uasset'
Select-TreePath $win @('ScaleBox_0', 'Main', 'Window', 'Page_1', 'MatchTime', 'Normal', 'Normal_Display', 'Img_MatchTimeBg_1')
Shot $win '2_matchtime_scorebox'

# 3) library widget uiImage (blocked)
Open-Package $win 'library/uiImage' 'library/uiImage.uasset'
$tree = ById $win 'WidgetTreeView'
$first = $tree.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::TreeItem)))
Select-Item $first
Start-Sleep -Seconds 4
Shot $win '3_uiimage_blocked'

$p.CloseMainWindow() | Out-Null
Start-Sleep -Seconds 2
if (-not $p.HasExited) { $p.Kill() }
"done"
