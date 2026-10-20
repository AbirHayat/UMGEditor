# Step 6d UI run: the typed-text cue (Pending / Invalid / Esc), the bytes-to-patch tab (summary, Copy all, per-value Copy,
# live update on edit / undo / redo), and saves on the status line with no "Saved" dialog. Creates project $Project
# (work\edits\zz6d; delete it afterwards). Reads the clipboard to check what Copy puts there. Stops at the first failed check.
# usage: powershell -NoProfile -ExecutionPolicy Bypass -File drive6d.ps1 -Exe <UmgEditor.App.exe> -Out <folder for PNGs>
param([string]$Exe, [string]$Out, [string]$Project = 'zz6d')
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
    # 0) new, empty project: the bytes tab says there's nothing to patch
    SetText (ById $win 'NewProjectBox') $Project
    Start-Sleep -Milliseconds 500
    Invoke $create
    Start-Sleep -Seconds 2
    "title: $(Title)"
    Tab (BytesTab)
    "bytes tab: $(BytesHeader) / $(BytesSummary)"
    Check 'empty project: nothing to patch' ((BytesSummary) -like 'No edits in project*')
    Tab (TreeTab)

    # 1) typed-text cue on MatchTimeBase > Img_Plate Offsets.Right (stock 0), without pressing Enter
    Select-Package 'MatchTime/MatchTimeBase' 'MatchTimeBase.uasset'
    Select-Tree @('Img_Plate') | Out-Null
    $right = Field 'Offsets.Right'
    "Right: $(Value $right), status '$(ItemStatus $right)'"
    SetText $right '120'
    Start-Sleep -Milliseconds 500
    "typed 120: status '$(ItemStatus $right)', tooltip '$($right.Current.HelpText)'"
    Check 'typed 120 is Pending' ((ItemStatus $right) -eq 'Pending')
    Shot '1_typed_pending'
    SetText $right '1,5'
    Start-Sleep -Milliseconds 500
    "typed 1,5: status '$(ItemStatus $right)', tooltip '$($right.Current.HelpText)'"
    Check 'typed 1,5 is Invalid' ((ItemStatus $right) -eq 'Invalid')
    Shot '2_typed_invalid'
    SetText $right '0.0'
    Start-Sleep -Milliseconds 500
    Check 'typed 0.0 (the stock value) is Applied' ((ItemStatus $right) -eq 'Applied')
    SetText $right '77'
    Esc $right
    "after Esc: '$(Value $right)', status '$(ItemStatus $right)'"
    Check 'Esc reverts to the applied value' ((Value $right) -eq '0' -and (ItemStatus $right) -eq 'Applied')

    # 2) apply 100 (first edit asks), then the bytes tab
    TypeEnter $right '100'
    $d = Dialog 'First edit of MatchTimeBase'
    Answer $d 'OK'
    Start-Sleep -Seconds 3
    $right = Field 'Offsets.Right'
    "after Enter: '$(Value $right)', status '$(ItemStatus $right)', edit message: $(Info)"
    Check 'applied: no cue' ((Value $right) -eq '100' -and (ItemStatus $right) -ne 'Pending')
    Tab (BytesTab)
    "bytes tab: $(BytesHeader) / $(BytesSummary)"
    Check 'tab header counts 1 value' ((BytesHeader) -eq 'Bytes to patch (1)')
    Shot '3_bytes_one_value'
    Invoke (ById $win 'CopyBytesButton')
    $all = Clip
    "--- Copy all:"; ($all -split "`n" | Select-Object -First 16) -join "`n"
    foreach ($want in @('Bytes to patch: 1 value in 1 package', 'MatchTimeBase · unsaved', 'Img_Plate (CanvasPanelSlot_1) · Offsets.Right · float', 'offset   0xA64', '0 → 100', 'new hex  00 00 C8 42   (2 of 4 bytes change: 0xA66, 0xA67)', '0x000A60  00 00 00 00[00 00 00 00]0C 00 00 00 00 00 00 00')) {
        Check "Copy all has '$want'" ($all.Contains($want))
    }
    "status line: $(ActionStatus)"

    # 3) live: edit again with the bytes tab open (the inspector stays on the right)
    TypeEnter (Field 'Offsets.Right') '110'
    Start-Sleep -Seconds 2
    Invoke (ById $win 'CopyBytesButton')
    $all = Clip
    Check 'live update: 0 → 110, 00 00 DC 42' ($all.Contains('0 → 110') -and $all.Contains('new hex  00 00 DC 42'))
    Shot '4_bytes_live_110'
    $copy = (ById $win 'BytesList').FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition((Cond $AE::NameProperty 'Copy'), (Cond $AE::ControlTypeProperty $CT::Button))))
    Invoke $copy
    $one = Clip
    "--- row Copy:"; $one
    Check 'row Copy is that value only' ($one.StartsWith('  Img_Plate (CanvasPanelSlot_1) · Offsets.Right · float') -and -not $one.Contains('Bytes to patch'))
    "status line: $(ActionStatus)"

    # 4) leave the package with unsaved edits: Yes saves, no "Saved" dialog, the status line says it
    Select-Package 'MatchTime/MatchTime' '/MatchTime/MatchTime.uasset' -NoWait
    $d = Dialog 'Unsaved edits'
    Answer $d 'Yes'
    NoDialog 'Saved'
    Start-Sleep -Seconds 2
    $st = ActionStatus
    "status line: $st"
    Check 'status line names the save' ($st -like '*Saved MatchTimeBase (1 value, 2 bytes differ from stock)*')
    Check 'bytes tab shows it saved' ((BytesSummary) -like '1 value in 1 package*')
    Shot '5_saved_status_line'

    # 5) a second package: MatchTime score box Left 126 -> 146 (canary C1's bytes)
    Tab (TreeTab)
    Select-Tree @('ScaleBox_0', 'Main', 'Window', 'Page_1', 'MatchTime', 'Normal', 'Normal_Display', 'Img_MatchTimeBg_1') | Out-Null
    TypeEnter (Field 'Offsets.Left') '146'
    $d = Dialog 'First edit of MatchTime'
    Answer $d 'OK'
    Start-Sleep -Seconds 3
    Tab (BytesTab)
    "bytes tab: $(BytesHeader) / $(BytesSummary)"
    Check 'two packages' ((BytesSummary) -like '2 values in 2 packages*')
    Invoke (ById $win 'CopyBytesButton')
    $all = Clip
    Check 'MatchTime row: 0x133E23, 126 → 146' ($all.Contains('offset   0x133E23') -and $all.Contains('126 → 146') -and $all.Contains('Img_MatchTimeBg_1 (CanvasPanelSlot_27)'))
    Shot '6_bytes_two_packages'

    # 6) undo / redo follow in the tab (keys from the tab header, not a text box)
    (BytesTab).SetFocus()
    Keys '^z'
    "after Ctrl+Z: $(BytesSummary)"
    Check 'Ctrl+Z: back to one value' ((BytesSummary) -like '1 value in 1 package*')
    Shot '7_after_undo'
    Keys '^y'
    "after Ctrl+Y: $(BytesSummary)"
    Check 'Ctrl+Y: two again' ((BytesSummary) -like '2 values in 2 packages*')

    # 7) Ctrl+S: status line, no dialog
    Front; [System.Windows.Forms.SendKeys]::SendWait('^s')
    NoDialog 'Saved'
    Start-Sleep -Seconds 1
    $st = ActionStatus
    "status line: $st"
    "status tooltip: $((ById $win 'ActionStatusText').Current.HelpText -replace "`r?`n", ' / ')"
    Check 'Ctrl+S on the status line' ($st -like '*Saved MatchTime (1 value, 2 bytes differ from stock)*')
    Check 'title has no *' (-not (Title).EndsWith('*'))
    Shot '8_ctrl_s_status_line'

    # 8) nothing unsaved: close without a prompt
    [void]$p.CloseMainWindow()
    Start-Sleep -Seconds 3
    "exited without a prompt: $($p.HasExited)"
    Check 'closed' $p.HasExited
}
finally {
    if (-not $p.HasExited) { try { Shot 'zz_last' } catch {}; $p.Kill() }
}
"done"
