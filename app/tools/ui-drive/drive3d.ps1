# Branch experiment step 3d UI run: a value that isn't stored (MatchTime Text_Score_Home Offsets.Left, default 0) is
# editable; setting 20 shows the size-change warning and 'new tag', the Bytes tab lists the inserted tag (insertion
# point, tag hex, context, header changes), Ctrl+S saves the canary C10 chunk byte for byte. Creates project $Project
# (work\edits\zz3d; delete it afterwards). Reads the clipboard. Stops at the first failed check.
# usage: powershell -NoProfile -ExecutionPolicy Bypass -File drive3d.ps1 -Exe <UmgEditor.App.exe> -Out <folder for PNGs>
param([string]$Exe, [string]$Out, [string]$Project = 'zz3d', [string]$Edits = 'work\edits')
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
    SetText (ById $win 'NewProjectBox') $Project
    Start-Sleep -Milliseconds 500
    Invoke $create
    Start-Sleep -Seconds 2
    "title: $(Title)"

    # 1) MatchTime > Text_Score_Home: Offsets.Left isn't stored (default 0) but is an editable field now
    Select-Package 'MatchTime/MatchTime' '/MatchTime/MatchTime.uasset'
    Select-Tree @('ScaleBox_0', 'Main', 'Window', 'Page_1', 'MatchTime', 'Normal', 'Normal_Display', 'Mask', 'Text_Score_Home') | Out-Null
    $left = Field 'Offsets.Left'
    "Left: '$(Value $left)', status '$(ItemStatus $left)', enabled $($left.Current.IsEnabled)"
    Check 'not-stored Left is an editable text box showing the default 0' ((Value $left) -eq '0' -and $left.Current.IsEnabled)
    $states = $win.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::Text)) | ForEach-Object { $_.Current.Name }
    Check "the field's state says 'default'" ($states -contains 'default')
    Shot '1_inspector_default'

    # 2) set 20: the first edit asks, then the size-change warning shows under the edit message
    TypeEnter $left '20'
    $d = Dialog 'First edit of MatchTime'
    Answer $d 'OK'
    Start-Sleep -Seconds 3
    "edit message: $(Info)"
    $warnings = (ById $win 'EditWarningList').FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::Text)) | ForEach-Object { $_.Current.Name }
    $warnings | ForEach-Object { "  warning: $_" }
    Check 'size-change warning shown' (($warnings | Where-Object { $_ -like '*Offsets.Left isn''t stored in MatchTime*29-byte tag*' }).Count -eq 1)
    $states = $win.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::Text)) | ForEach-Object { $_.Current.Name }
    Check "the field's state says 'new tag'" ($states -contains 'new tag')
    Shot '2_inspector_new_tag'

    # 3) bytes tab: the inserted tag with its insertion point, context and header changes
    Tab (BytesTab)
    "bytes tab: $(BytesHeader) / $(BytesSummary)"
    Check 'tab header counts 1 value' ((BytesHeader) -eq 'Bytes to patch (1)')
    Check 'summary says new tag' ((BytesSummary) -like '*added as a new 29-byte tag*')
    Invoke (ById $win 'CopyBytesButton')
    $all = Clip
    "--- Copy all:"; $all
    foreach ($want in @('MatchTime · unsaved · 1,269,266 → 1,269,295 bytes (+29: 1 new tag)', 'Text_Score_Home (CanvasPanelSlot_0) · Offsets.Left · new float tag (29 bytes)',
                        'insert   0x1356B5 (stock offset), in front of the Right tag', 'value    default 0 → 20',
                        'tag hex  BD 00 00 00 00 00 00 00 9A 00 00 00 00 00 00 00 04 00 00 00 00 00 00 00 00 00 00 A0 41',
                        'export 11 CookedSerialSize 271 → 300', "1,461 later exports' CookedSerialOffset +29")) {
        Check "Copy all has '$want'" ($all.Contains($want))
    }
    Check 'the context marks the insertion point' ($all -match '0x1356B0 .*\|')
    $at = ById $win 'InsertAt_Offsets.Left' 10
    "insert row: $(Value $at)"
    Check 'the insert row is in the UI' ((Value $at) -like '0x1356B5 *')
    Shot '3_bytes_insert'

    # 4) Ctrl+S: saved, status line names the new tag, the file is the device-tested C10 chunk
    Front; [System.Windows.Forms.SendKeys]::SendWait('^s')
    NoDialog 'Saved'
    Start-Sleep -Seconds 2
    $st = ActionStatus
    "status line: $st"
    Check 'status line: saved with 1 new tag' ($st -like '*Saved MatchTime (1 value, 1616 bytes differ from stock, 1 new tag: +29 bytes)*re-parsed*')
    $file = Get-ChildItem -Recurse (Join-Path $Edits $Project) -Filter 'MatchTime.edited.uasset' | Select-Object -First 1
    $sha = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "saved $($file.FullName) $($file.Length) bytes sha256 $sha"
    Check 'saved chunk = canary C10 chunk' ($sha -eq '69fde3309e907969b6dea25f29a20d42f6c1cf1e2d3c1c86306bd8cdf27473e2')
    Shot '4_saved'

    # 5) undo to stock: the bytes tab is back to no values, nothing grows
    (BytesTab).SetFocus()
    Keys '^z'
    "after Ctrl+Z: $(BytesSummary)"
    Check 'Ctrl+Z: no tag left' ((BytesSummary) -notlike '*29-byte tag*')
    Keys '^y'
    Front; [System.Windows.Forms.SendKeys]::SendWait('^s')
    Start-Sleep -Seconds 2
    [void]$p.CloseMainWindow()
    Start-Sleep -Seconds 3
    Check 'closed without a prompt' $p.HasExited
}
finally {
    if (-not $p.HasExited) { try { Shot 'zz_last' } catch {}; $p.Kill() }
}
"done"
