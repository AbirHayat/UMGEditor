# Step 6e UI run: the impact panel (every package an edit reaches, changed boxes in px and design units, online
# packages tagged and collapsed, pending-edit warnings), Preview from the panel with the edits (stock toggle, live
# update, back to the open package). Creates project $Project (work\edits\zz6e; delete it afterwards). Reads the
# clipboard to check the report. Stops at the first failed check.
# usage: powershell -NoProfile -ExecutionPolicy Bypass -File drive6e.ps1 -Exe <UmgEditor.App.exe> -Out <folder for PNGs>
param([string]$Exe, [string]$Out, [string]$Project = 'zz6e')
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
using System.Text;
public static class W {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr dlg, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, StringBuilder l);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    // MessageBox: the text is control 0xFFFF; buttons are IDOK 1, IDCANCEL 2, IDYES 6, IDNO 7
    public static string Text(IntPtr dlg) { var sb = new StringBuilder(8192); SendMessage(GetDlgItem(dlg, 0xFFFF), 0x000D, (IntPtr) 8192, sb); return sb.ToString(); }
    public static bool Press(IntPtr dlg, int id) { return GetDlgItem(dlg, id) != IntPtr.Zero && PostMessage(dlg, 0x0111, (IntPtr) id, GetDlgItem(dlg, id)); }
}
"@
$uia = [System.Windows.Automation.AutomationElement].Assembly.Location
$uiat = [System.Windows.Automation.ControlType].Assembly.Location
Add-Type -ReferencedAssemblies $uia, $uiat -TypeDefinition @"
using System.Threading.Tasks; using System.Windows.Automation;
public static class Later {
    // Select() blocks until the app's handler returns; a modal prompt would block it, so select from another thread
    public static string Error = "";
    public static void Select(AutomationElement e) { Task.Run(() => { try { ((SelectionItemPattern) e.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); } catch (System.Exception x) { Error = x.GetType().Name + ": " + x.Message; } }); }
}
"@
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

function Cond($prop, $value) { New-Object System.Windows.Automation.PropertyCondition($prop, $value) }
function Find($root, $prop, $value, $timeout = 60) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeout) {
        $e = $root.FindFirst($TS::Descendants, (Cond $prop $value))
        if ($e) { return $e }
        Start-Sleep -Milliseconds 300
    }
    throw "not found: $value"
}
function ById($root, $id, $t = 60) { Find $root $AE::AutomationIdProperty $id $t }
function ByName($root, $name, $t = 30) { Find $root $AE::NameProperty $name $t }
function Select-Item($e) { $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Expand($e) { try { $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() } catch {} }
function Invoke($e) { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function SetText($e, $text) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text) }
function Front { [W]::SetForegroundWindow([IntPtr]$win.Current.NativeWindowHandle) | Out-Null; Start-Sleep -Milliseconds 300 }
function Shot($name) {
    Start-Sleep -Milliseconds 700
    if (-not $script:rect) { $script:rect = $win.Current.BoundingRectangle }
    $r = $script:rect
    $bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
    $bmp.Save("$Out\$name.png"); $g.Dispose(); $bmp.Dispose()
    "shot $name"
}
function Title { $win.Current.Name }
# a MessageBox of the app, by its caption (#32770 dialog)
function Dialog($caption, $timeout = 20) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeout) {
        $h = [W]::FindWindow('#32770', $caption)
        if ($h -ne [IntPtr]::Zero -and (([W]::GetDlgItem($h, 2) -ne [IntPtr]::Zero) -or ([W]::GetDlgItem($h, 1) -ne [IntPtr]::Zero))) { Start-Sleep -Milliseconds 400; return $h }
        Start-Sleep -Milliseconds 300
    }
    throw "dialog not shown: $caption (async select error: $([Later]::Error))"
}
function DialogText($h) { ([W]::Text($h)) -replace "`r?`n", ' / ' }
function Answer($h, $button) {
    $id = @{ OK = 1; Cancel = 2; Yes = 6; No = 7 }[$button]
    if (-not [W]::Press($h, $id) -and -not ($button -eq "OK" -and [W]::Press($h, 2))) { throw "no $button button" }
    Start-Sleep -Milliseconds 500
}
# Enter in a field: focus it and send the key (the handler may open a modal dialog, so don't wait on it)
function TypeEnter($field, $text) {
    SetText $field $text; Front; $field.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
    Start-Sleep -Milliseconds 800
}
function Keys($keys) { Front; [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Seconds 2 }
function Select-Package($search, $fileSuffix, [switch]$NoWait) {
    SetText (ById $win 'SearchBox') $search
    Start-Sleep -Milliseconds 900
    $items = $win.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::DataItem))
    $hit = $items | Where-Object { $_.Current.Name -like "*$fileSuffix*" } | Select-Object -First 1
    if (-not $hit) { throw "package $fileSuffix not in list" }
    if ($NoWait) { [Later]::Select($hit); Start-Sleep -Seconds 1 }
    else { Select-Item $hit; Start-Sleep -Seconds 3 }
}
function Select-Tree([string[]]$names) {
    $node = ById $win 'WidgetTreeView'
    foreach ($n in $names) { $node = ByName $node $n 20; if ($n -ne $names[-1]) { Expand $node; Start-Sleep -Milliseconds 400 } }
    Select-Item $node
    Start-Sleep -Seconds 3
    $node
}
function Field($path) { ById $win "Field_$path" 15 }
function Info { (ById $win 'EditMessageText' 5).Current.Name }
function ActionStatus { (ById $win 'ActionStatusText' 5).Current.Name }
# since 6d a successful save opens no dialog
function NoDialog($caption) { Start-Sleep -Milliseconds 1500; if ([W]::FindWindow('#32770', $caption) -ne [IntPtr]::Zero) { throw "unexpected dialog: $caption" } }

# ---- 6d ----
function ItemStatus($e) { $e.Current.ItemStatus }
function Value($e) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Tab($e) { Select-Item $e; Start-Sleep -Seconds 2 }
function BytesTab { ById $win 'BytesTab' }
function TreeTab { ByName (ById $win 'MiddleTabs') 'Tree' }
function BytesSummary { (ById $win 'BytesSummaryText' 10).Current.Name }
function BytesHeader { (BytesTab).Current.Name }
function Clip { Start-Sleep -Milliseconds 500; (Get-Clipboard -Raw) -replace "`r", '' }
function Check($what, [bool]$ok) { if (-not $ok) { throw "CHECK FAILED: $what" }; "  ok: $what" }
function Esc($field) { Front; $field.SetFocus(); [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 500 }

# ---- 6e ----
function ImpactTab { ById $win 'ImpactTab' }
function PreviewTab { ByName (ById $win 'MiddleTabs') 'Preview' }
function ImpactSummary { (ById $win 'ImpactSummaryText' 10).Current.Name }
function ImpactHeader { (ImpactTab).Current.Name }
function IsSelectedTab($e) { $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected }
function ExpandState($e) { $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState }
function Toggle($e) { $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Seconds 2 }
function Present($id) { [bool]$win.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty $id)) }
# the report is laid out in the background after each change: wait until the summary matches
function WaitImpact($like, $timeout = 30) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeout) { if ((ImpactSummary) -like $like) { return }; Start-Sleep -Milliseconds 500 }
    throw "impact summary not '$like': $(ImpactSummary)"
}
function CopyImpact { Invoke (ById $win 'CopyImpactButton'); Clip }
# scroll the impact list (0 = top, 100 = bottom) so a screenshot shows that part
function ScrollImpact($percent) {
    $sv = (ImpactTab).FindFirst($TS::Descendants, (Cond $AE::IsScrollPatternAvailableProperty $true))
    $sv.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1, $percent); Start-Sleep -Milliseconds 600
}

$p = Start-Process $Exe -PassThru
$win = $null
$sw = [Diagnostics.Stopwatch]::StartNew()
while (-not $win -and $sw.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 500; $p.Refresh(); if ($p.MainWindowHandle -ne 0) { $win = $AE::FromHandle($p.MainWindowHandle) } }
if (-not $win) { throw "no window" }
try { $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState('Maximized') } catch {}
$create = ById $win 'CreateProjectButton' 60
Start-Sleep -Seconds 1
$script:rect = $win.Current.BoundingRectangle
$sw.Restart()
while (-not $create.Current.IsEnabled -and $sw.Elapsed.TotalSeconds -lt 120) { SetText (ById $win 'NewProjectBox') $Project; Start-Sleep -Seconds 1 }

try {
    # 0) new, empty project: nothing is affected
    SetText (ById $win 'NewProjectBox') $Project
    Start-Sleep -Milliseconds 500
    Invoke $create
    Start-Sleep -Seconds 2
    "title: $(Title)"
    Tab (ImpactTab)
    "impact tab: $(ImpactHeader) / $(ImpactSummary)"
    Check 'empty project: nothing affected' ((ImpactSummary) -like 'No edits in project*')
    Tab (TreeTab)

    # 1) canary C6's shared-plate edit: MatchTimeBase > Img_Plate Offsets.Right 0 -> 100
    Select-Package 'MatchTime/MatchTimeBase' 'MatchTimeBase.uasset'
    Select-Tree @('Img_Plate') | Out-Null
    TypeEnter (Field 'Offsets.Right') '100'
    $d = Dialog 'First edit of MatchTimeBase'
    Answer $d 'OK'
    Start-Sleep -Seconds 2
    Tab (ImpactTab)
    WaitImpact '9 packages reached (1 edited, 4 online)*'
    "impact tab: $(ImpactHeader) / $(ImpactSummary)"
    Check 'tab header counts 9 packages' ((ImpactHeader) -eq 'Impact (9)')
    $online = ById $win 'ImpactOnlineExpander'
    "online expander: $(ExpandState $online) / $($online.Current.Name)"
    Check 'online packages collapsed' ((ExpandState $online) -eq 'Collapsed')
    Check 'offline packages listed' ((Present 'ImpactPreview_MatchTimeGoal') -and (Present 'ImpactPreview_MatchTimeCard') -and (Present 'ImpactPreview_MatchTime'))
    Shot '1_impact_offline'
    $all = CopyImpact
    "--- Copy all (first lines):"; ($all -split "`n" | Select-Object -First 14) -join "`n"
    foreach ($want in @('Impact: 9 packages reached (1 edited, 4 online), 9 with changed boxes',
                        'MatchTime · Offline scoreboard · via MatchTimeBase → MatchTime',
                        'right 388.9 → 296.3 px (−92.6 px = −100 u; 420 → 320 u)',
                        'right 555.6 → 463 px (−92.6 px = −100 u',
                        'right 544.4 → 451.9 px (−92.6 px = −100 u',
                        'MatchTimeConnection [online]')) {
        Check "Copy all has '$want'" ($all.Contains($want))
    }

    # 2) the online versions, expanded
    $online.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Seconds 1
    Check 'online packages listed when expanded' ((Present 'ImpactPreview_MatchTimeConnection') -and (Present 'ImpactPreview_MatchTimeConnectionGoal'))
    ScrollImpact 100
    Shot '2_impact_online_expanded'
    ScrollImpact 0
    $online.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    Start-Sleep -Milliseconds 500

    # 3) click a package: the goal banner in the Preview tab with the edits, changed boxes in green
    Invoke (ById $win 'ImpactPreview_MatchTimeGoal')
    Start-Sleep -Seconds 4
    $banner = (ById $win 'ImpactBannerText' 10).Current.Name
    "banner: $banner"
    Check 'Preview tab selected' (IsSelectedTab (PreviewTab))
    Check 'banner names MatchTimeGoal' ($banner -like '*MatchTimeGoal*via MatchTimeBase → MatchTimeGoal*')
    "status line: $(ActionStatus)"
    Shot '3_preview_goal_edited'
    Toggle (ById $win 'StockCheck')
    Shot '4_preview_goal_stock'
    Toggle (ById $win 'StockCheck')

    # 4) live: a second value for the plate; the panel and the shown package follow
    Invoke (ById $win 'BackToImpactButton')
    Start-Sleep -Seconds 1
    Check 'back to the Impact tab' (IsSelectedTab (ImpactTab))
    Tab (TreeTab)
    TypeEnter (Field 'Offsets.Right') '150'
    Start-Sleep -Seconds 2
    Tab (ImpactTab)
    Start-Sleep -Seconds 3
    $all = CopyImpact
    Check 'live: the plate is now 150 units shorter' ($all.Contains('right 388.9 → 250 px (−138.9 px = −150 u'))
    Tab (PreviewTab)
    $banner = (ById $win 'ImpactBannerText' 10).Current.Name
    "banner after the second edit: $banner"
    Check 'the preview still shows MatchTimeGoal' ($banner -like '*MatchTimeGoal*')
    Shot '5_preview_goal_150'
    Invoke (ById $win 'ShowOpenPackageButton')
    Start-Sleep -Seconds 3
    Check 'Open package: banner gone' (-not (Present 'ImpactBannerText'))
    Shot '6_preview_open_package'

    # 5) a pending-edit warning in the panel: canary 1's Mask Left 829 (the score digits vanish in game)
    Tab (TreeTab)
    Select-Package 'MatchTime/MatchTime' '/MatchTime/MatchTime.uasset' -NoWait
    $d = Dialog 'Unsaved edits'
    Answer $d 'Yes'
    NoDialog 'Saved'
    Start-Sleep -Seconds 3
    Select-Tree @('ScaleBox_0', 'Main', 'Window', 'Page_1', 'MatchTime', 'Normal', 'Normal_Display', 'Mask') | Out-Null
    TypeEnter (Field 'Offsets.Left') '829'
    $d = Dialog 'First edit of MatchTime'
    Answer $d 'OK'
    Start-Sleep -Seconds 2
    $inspector = ((ById $win 'EditWarningList').FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::Text)) | ForEach-Object { $_.Current.Name }) -join ' '
    "inspector warnings: $inspector"
    Check 'inspector: Danger for Mask, and the earlier plate edit is not "another widget moving"' ($inspector -like '*Mask ends up completely outside*' -and $inspector -notlike '*other widget*')
    Tab (ImpactTab)
    WaitImpact '9 packages reached (2 edited, 4 online)*'
    $all = CopyImpact
    $warn = ($all -split "`n" | Where-Object { $_ -like '*Mask ends up*' }) -join ' / '
    "warning: $warn"
    Check 'Danger: Mask ends up completely outside' ($all.Contains('[Danger] Mask ends up completely outside'))
    ScrollImpact 0
    Shot '7_impact_leaves_clip'

    # 6) close with unsaved edits: No closes without saving
    [void]$p.CloseMainWindow()
    $d = Dialog 'Unsaved edits'
    Answer $d 'No'
    Start-Sleep -Seconds 3
    "exited after No: $($p.HasExited)"
    Check 'closed' $p.HasExited
}
finally {
    if (-not $p.HasExited) { try { Shot 'zz_last' } catch {}; $p.Kill() }
}
"done"
