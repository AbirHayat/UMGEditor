# Step 6f UI run: dragging in the preview with real mouse input (SetCursorPos / mouse_event; 100 % display scale):
# handles on the score box, a 20-unit move with the live label and the first-edit prompt, one undo step, an Esc-cancelled
# edge resize, Ctrl for free movement, arrow-key nudges, a live Danger while dragging Mask out of its clip, the reason
# instead of handles on an embedded row, and a stretched plate (Right/Bottom only) dragged to canary C6's bytes.
# Creates project $Project (work\edits\zz6f; delete it afterwards). Stops at the first failed check. Don't touch the
# mouse while it runs.
# usage: powershell -NoProfile -ExecutionPolicy Bypass -File drive6f.ps1 -Exe <UmgEditor.App.exe> -Out <folder for PNGs>
param([string]$Exe, [string]$Out, [string]$Project = 'zz6f')
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

# ---- 6f ----
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Mouse {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    public static void Down() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); }
    public static void Up() { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
    public static void Ctrl(bool down) { keybd_event(0x11, 0, down ? 0u : 2u, UIntPtr.Zero); }
}
"@
function PreviewTab { ByName (ById $win 'MiddleTabs') 'Preview' }
function Canvas { ById $win 'PreviewCanvas' }
function CanvasStatus { (Canvas).Current.ItemStatus }
function Value($e) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function FieldValue($path) { Value (Field $path) }
function Text($id) { (ById $win $id 10).Current.Name }
# the selected widget's box in screen px: canvas origin + the canvas-local rect it reports (100 % display scale)
function Box {
    $s = CanvasStatus
    if ($s -notmatch 'box ([\d.-]+),([\d.-]+),([\d.-]+),([\d.-]+)') { throw "no selected box: '$s'" }
    $c = (Canvas).Current.BoundingRectangle
    [pscustomobject]@{ L = $c.X + [double]$Matches[1]; T = $c.Y + [double]$Matches[2]; R = $c.X + [double]$Matches[3]; B = $c.Y + [double]$Matches[4] }
}
# screen px per design unit in the preview: the canvas fits 1786x1000 viewport px, 0.925926 px per unit
function PxPerUnit { $c = (Canvas).Current.BoundingRectangle; [Math]::Min($c.Width / 1786, $c.Height / 1000) * 0.925926 }
# press at (x0, y0), move to (x1, y1) in steps, run $during while held, then release (or Esc first)
function DragFrom($x0, $y0, $x1, $y1, [scriptblock]$during = $null, [switch]$Ctrl, [switch]$Escape) {
    Front
    [void][Mouse]::SetCursorPos([int]$x0, [int]$y0); Start-Sleep -Milliseconds 200
    [Mouse]::Down(); Start-Sleep -Milliseconds 150
    if ($Ctrl) { [Mouse]::Ctrl($true) }
    foreach ($i in 1..12) { [void][Mouse]::SetCursorPos([int]($x0 + ($x1 - $x0) * $i / 12), [int]($y0 + ($y1 - $y0) * $i / 12)); Start-Sleep -Milliseconds 40 }
    Start-Sleep -Milliseconds 700
    if ($during) { & $during }
    if ($Escape) { [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Milliseconds 400 }
    if ($Ctrl) { [Mouse]::Ctrl($false) }
    [Mouse]::Up(); Start-Sleep -Milliseconds 900
}
# the units in the live label: "Offsets.Left 126 → 147 (+21 u)" -> 21
function LabelUnits { if ((Text 'DragLabelText') -match '\(([+−-])([\d.]+) u\)') { $v = [double]$Matches[2]; if ($Matches[1] -eq '+') { $v } else { -$v } } }
# a horizontal drag of about $units that ends where the live label shows exactly $label units; $sign = +1 when the
# label's value grows with the drag, -1 when it shrinks (a right margin grows as its edge goes left)
function DragToUnits([double]$x0, [double]$y, [double]$units, [double]$label, [int]$sign) {
    Front
    [void][Mouse]::SetCursorPos([int]$x0, [int]$y); Start-Sleep -Milliseconds 200
    [Mouse]::Down(); Start-Sleep -Milliseconds 150
    $px = [int][Math]::Round($units * (PxPerUnit))
    foreach ($i in 1..12) { [void][Mouse]::SetCursorPos([int]($x0 + $px * $i / 12), [int]$y); Start-Sleep -Milliseconds 40 }
    Start-Sleep -Milliseconds 600
    foreach ($try in 1..6) {
        $n = LabelUnits
        if ($n -eq $label) { break }
        if ($n -lt $label) { $px += $sign } else { $px -= $sign }
        [void][Mouse]::SetCursorPos([int]($x0 + $px), [int]$y); Start-Sleep -Milliseconds 500
    }
    $final = Text 'DragLabelText'
    [Mouse]::Up(); Start-Sleep -Milliseconds 900
    $final
}
function UndoEnabled { (ById $win 'UndoButton').Current.IsEnabled }
function ShowPreview { Tab (PreviewTab); Start-Sleep -Seconds 2 }

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

$scoreBox = @('ScaleBox_0', 'Main', 'Window', 'Page_1', 'MatchTime', 'Normal', 'Normal_Display', 'Img_MatchTimeBg_1')
try {
    SetText (ById $win 'NewProjectBox') $Project
    Start-Sleep -Milliseconds 500
    Invoke $create
    Start-Sleep -Seconds 2
    "title: $(Title)"

    # 1) MatchTime score box: every handle, the hint
    Select-Package 'MatchTime/MatchTime' '/MatchTime/MatchTime.uasset'
    Select-Tree $scoreBox | Out-Null
    ShowPreview
    "canvas: $(CanvasStatus)"
    "hint: $(Text 'DragHintText')"
    Check 'score box: all five handles' ((CanvasStatus) -like '*handles Move,Left,Top,Right,Bottom*')
    Check 'hint explains dragging' ((Text 'DragHintText') -like 'Drag the selected widget*Ctrl*Esc*arrow keys nudge 1 unit*')
    Shot '1_handles_score_box'

    # 2) move by 20 units (+0.3 px jitter that snapping removes): live label while held, first-edit prompt on release
    $b = Box; $u = PxPerUnit
    "box on screen: $($b.L),$($b.T) - $($b.R),$($b.B); $([Math]::Round($u, 4)) screen px per unit"
    $cx = ($b.L + $b.R) / 2; $cy = ($b.T + $b.B) / 2
    $script:label = $null; $script:n = $null; $script:during = $null
    DragFrom $cx $cy ($cx + [Math]::Round(20 * $u)) $cy {
        $script:label = Text 'DragLabelText'; $script:n = LabelUnits; $script:during = CanvasStatus
        "while dragging: $script:label / canvas: $script:during"
        Shot '2_dragging_move'
    }
    $left = 126 + $script:n
    "the drag snapped to $($script:n) whole units (≈ $([Math]::Round(1 / $u, 1)) units per screen px at this zoom)"
    Check 'live label: whole units' ($script:label -eq "Offsets.Left 126 → $left (+$($script:n) u)")
    Check 'the box stays under the cursor vertically while dragging' ($script:during -match "box [\d.]+,$([regex]::Escape(($b.T - (Canvas).Current.BoundingRectangle.Y).ToString('0.#', [Globalization.CultureInfo]::InvariantCulture))),")
    $d = Dialog 'First edit of MatchTime'
    Answer $d 'OK'
    Start-Sleep -Seconds 3
    "Left after the drag: $(FieldValue 'Offsets.Left'); message: $(Info)"
    Check "drag stored Left = $left" ((FieldValue 'Offsets.Left') -eq "$left")
    Check 'reported as a drag' ((Info) -like "*Offsets.Left 126 → $left (+$($script:n) u) (drag)*")
    Shot '3_after_drag'

    # 3) one undo step: Ctrl+Z back to 126 with nothing left to undo, Ctrl+Y again
    (Canvas).SetFocus()
    Keys '^z'
    Check 'Ctrl+Z: Left 126, nothing left to undo' ((FieldValue 'Offsets.Left') -eq '126' -and -not (UndoEnabled))
    Keys '^y'
    Check "Ctrl+Y: Left $left" ((FieldValue 'Offsets.Left') -eq "$left")

    # 4) right edge +30 units, Esc while held: nothing changes
    ShowPreview
    $b = Box
    DragFrom $b.R (($b.T + $b.B) / 2) ($b.R + 30 * $u) (($b.T + $b.B) / 2) {
        "resizing: $(Text 'DragLabelText')"
        Shot '4_dragging_right_edge'
    } -Escape
    "after Esc: Right $(FieldValue 'Offsets.Right'); status: $(ActionStatus)"
    Check 'Esc cancelled: Right still 120' ((FieldValue 'Offsets.Right') -eq '120' -and (ActionStatus) -like '*Drag cancelled*')

    # 5) Ctrl = free movement: 10.5 units stays fractional; then undo it
    $b = Box; $cx = ($b.L + $b.R) / 2; $cy = ($b.T + $b.B) / 2
    DragFrom $cx $cy ($cx + 10.5 * $u) $cy { "free: $(Text 'DragLabelText')" } -Ctrl
    $free = FieldValue 'Offsets.Left'
    "Left after a free drag: $free"
    Check 'free drag: not a whole unit' ($free -match '^\d+\.\d+$' -and [Math]::Abs([double]$free - ($left + 10.5)) -lt 2)
    (Canvas).SetFocus(); Keys '^z'
    Check "undo the free drag: $left" ((FieldValue 'Offsets.Left') -eq "$left")

    # 6) arrow keys: two presses right, one up = three undo steps
    (Canvas).SetFocus()
    Keys '{RIGHT}'; Keys '{RIGHT}'; Keys '{UP}'
    "after nudges: Left $(FieldValue 'Offsets.Left'), Top $(FieldValue 'Offsets.Top')"
    Check "nudged: Left $($left + 2), Top 8" ((FieldValue 'Offsets.Left') -eq "$($left + 2)" -and (FieldValue 'Offsets.Top') -eq '8')
    (Canvas).SetFocus(); Keys '^z'; Keys '^z'; Keys '^z'
    Check "three undo steps back to Left $left, Top 9" ((FieldValue 'Offsets.Left') -eq "$left" -and (FieldValue 'Offsets.Top') -eq '9')

    # 7) live warning: drag Mask out of Normal_Display (canary 1), Esc
    Tab (TreeTab)
    Select-Tree @('ScaleBox_0', 'Main', 'Window', 'Page_1', 'MatchTime', 'Normal', 'Normal_Display', 'Mask') | Out-Null
    ShowPreview
    $b = Box; $cx = ($b.L + $b.R) / 2; $cy = ($b.T + $b.B) / 2
    $script:warn = ''
    DragFrom $cx $cy ($cx + 700 * $u) $cy {
        $script:warn = ((ById $win 'DragWarningList').FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::Text)) | ForEach-Object { $_.Current.Name }) -join ' '
        "live warnings: $script:warn"
        Shot '5_live_warning_mask'
    } -Escape
    Check 'live Danger while dragging Mask out' ($script:warn -like '*Danger*Mask ends up completely outside Normal_Display*')
    Check 'Esc: Mask Left still 129' ((FieldValue 'Offsets.Left') -eq '129')

    # 8) an embedded row (read-only): the reason instead of handles
    Tab (TreeTab)
    Select-Tree ($scoreBox + @('CanvasPanel_0', 'Img_Plate')) | Out-Null
    ShowPreview
    "canvas: $(CanvasStatus)"
    "hint: $(Text 'DragHintText')"
    Check 'embedded row: a reason, no handles' ((CanvasStatus) -like '*reason*' -and (CanvasStatus) -notlike '*handles*')
    Check 'hint names the owning package' ((Text 'DragHintText') -like "Can't drag Img_Plate*Open MatchTimeBase_1 to edit it*")
    Shot '6_reason_instead_of_handles'

    # 9) a stretched axis: MatchTimeBase's plate offers only Right and Bottom; drag Right 100 units left (= C6)
    Select-Package 'MatchTime/MatchTimeBase' 'MatchTimeBase.uasset' -NoWait
    $d = Dialog 'Unsaved edits'
    Answer $d 'Yes'
    NoDialog 'Saved'
    # the search for MatchTimeBase hides MatchTime from the list: that alone is no switch, so no second prompt
    NoDialog 'Unsaved edits'
    "  ok: no second prompt when the filter hides the saved package"
    Start-Sleep -Seconds 3
    Tab (TreeTab)
    Select-Tree @('Img_Plate') | Out-Null
    ShowPreview
    "canvas: $(CanvasStatus)"
    Check 'stretched plate: Right and Bottom only' ((CanvasStatus) -like '*handles Right,Bottom')
    Shot '7_stretched_handles'
    $b = Box
    $stretched = DragToUnits ($b.R - 2) (($b.T + $b.B) / 2) -100 100 -1
    "stretched drag: $stretched"
    $d = Dialog 'First edit of MatchTimeBase'
    Answer $d 'OK'
    Start-Sleep -Seconds 3
    Check 'stretched drag: Right margin 100 (canary C6)' ((FieldValue 'Offsets.Right') -eq '100')
    Tab (ById $win 'BytesTab')
    Invoke (ById $win 'CopyBytesButton')
    $all = Clip
    Check 'bytes: 0xA64 00 00 C8 42 (canary C6)' ($all.Contains('offset   0xA64') -and $all.Contains('new hex  00 00 C8 42'))
    Shot '8_stretched_bytes'

    [void]$p.CloseMainWindow()
    $d = Dialog 'Unsaved edits'
    Answer $d 'No'
    Start-Sleep -Seconds 3
    Check 'closed' $p.HasExited
}
finally {
    if (-not $p.HasExited) { try { Shot 'zz_last' } catch {}; $p.Kill() }
}
"done"
