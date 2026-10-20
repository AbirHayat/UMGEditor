# Step 6c UI run (editing): creates project $Project (work\edits\zz6c; delete it afterwards), edits MatchTimeBase >
# Img_Plate Offsets.Right, a refused input, the package/project switch prompts (Cancel / Yes), preview, Ctrl+Z/Y/S and the
# close prompt. Since 6d a save shows on the status line (ActionStatusText) instead of a "Saved" dialog.
# usage: powershell -NoProfile -ExecutionPolicy Bypass -File drive6c.ps1 -Exe <UmgEditor.App.exe> -Out <folder for PNGs>
param([string]$Exe, [string]$Out, [string]$Project = 'zz6c')
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
    "title at start: $(Title)"
    # 1) new project
    SetText (ById $win 'NewProjectBox') $Project
    Start-Sleep -Milliseconds 500
    Invoke $create
    Start-Sleep -Seconds 2
    "title after New: $(Title)"
    Shot '1_new_project'

    # 2) MatchTimeBase > Img_Plate, type 100 into Offsets.Right -> first-edit confirmation
    Select-Package 'MatchTime/MatchTimeBase' 'MatchTimeBase.uasset'
    Select-Tree @('Img_Plate') | Out-Null
    $right = Field 'Offsets.Right'
    "Right before: $($right.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value)"
    TypeEnter $right '100'
    $d = Dialog 'First edit of MatchTimeBase'
    "confirm: $(DialogText $d)"
    Shot '2_first_edit_confirm'
    Answer $d 'OK'
    Start-Sleep -Seconds 3
    "title after edit: $(Title)"
    "edit message: $(Info)"
    "Right after: $((Field 'Offsets.Right').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value)"
    Shot '3_edited'

    # 3) bad input and a grey field
    TypeEnter (Field 'Offsets.Right') '1,5'
    "bad input: $(Info)"
    Shot '4_refused'

    # 4) switch package with unsaved edits: Cancel stays, Yes saves (status line) and opens the other one
    Select-Package 'MatchTime/MatchTime' '/MatchTime/MatchTime.uasset' -NoWait
    $d = Dialog 'Unsaved edits'
    "switch prompt: $(DialogText $d)"
    Shot '5_switch_prompt'
    Answer $d 'Cancel'
    Start-Sleep -Seconds 2
    $sel = $win.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::DataItem)) | Where-Object { try { $_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } catch { $false } } | ForEach-Object { ($_.Current.Name -split ', ')[1] }
    "selected after Cancel: $sel ; tree still has Img_Plate: $([bool]((ById $win 'WidgetTreeView').FindFirst($TS::Descendants, (Cond $AE::NameProperty 'Img_Plate'))))"
    Shot '5b_after_cancel'
    Select-Package 'MatchTime/MatchTime' '/MatchTime/MatchTime.uasset' -NoWait
    $d = Dialog 'Unsaved edits'
    Answer $d 'Yes'
    NoDialog 'Saved'
    "save status: $(ActionStatus)"
    Shot '6_saved_status'
    Start-Sleep -Seconds 3
    "title after save: $(Title)"

    # 5) preview of MatchTime with the edited shared plate (blue dashed)
    Select-Item (ByName $win 'Preview')
    Start-Sleep -Seconds 5
    Shot '7_preview_edited'
    Select-Item (ByName $win 'Tree')
    Start-Sleep -Seconds 1

    # 6) Ctrl+Z / Ctrl+Y / Ctrl+S from the tree
    $node = Select-Tree @('ScaleBox_0')
    $node.SetFocus()
    Keys '^z'
    "title after Ctrl+Z: $(Title)"
    Keys '^y'
    "title after Ctrl+Y: $(Title)"
    Keys '^z'
    "title after Ctrl+Z: $(Title)"
    Front; [System.Windows.Forms.SendKeys]::SendWait('^s')
    NoDialog 'Saved'
    "Ctrl+S status: $(ActionStatus)"
    Start-Sleep -Seconds 1
    "title after Ctrl+S: $(Title)"

    # 7) project switch with unsaved edits: Cancel keeps the project and the picker shows it again
    Keys '^y'
    $picker = ById $win 'ProjectPicker'
    Expand $picker; Start-Sleep -Milliseconds 600
    [Later]::Select((ByName $picker 'default' 10)); Start-Sleep -Seconds 1
    $d = Dialog 'Unsaved edits'
    "project prompt: $(DialogText $d)"
    Answer $d 'Cancel'
    Start-Sleep -Seconds 2
    try { $picker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch {}
    Start-Sleep -Milliseconds 500
    "picker after Cancel: $($picker.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection() | ForEach-Object { $_.Current.Name }) ; title $(Title)"
    Shot '7b_project_cancel'

    # 8) close with unsaved edits: the prompt, then No closes
    "title before close: $(Title)"
    [void]$p.CloseMainWindow()
    $d = Dialog 'Unsaved edits'
    "close prompt: $(DialogText $d)"
    Shot '8_close_prompt'
    Answer $d 'No'
    Start-Sleep -Seconds 3
    "exited after No: $($p.HasExited)"
}
finally {
    if (-not $p.HasExited) { try { Shot 'zz_last' } catch {}; $p.Kill() }
}
"done"
