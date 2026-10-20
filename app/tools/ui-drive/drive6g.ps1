# Step 6g UI run: the export dialog. Refused while an edit is unsaved (Build disabled, the dirty package unticked), the
# retoc hash shown, a container without _P and a bad mod name refused; a retoc whose verify fails (-FakeRetoc, a .cmd
# wrapper around the real retoc) aborts the build and removes mod\; the real retoc builds canary C6's container in the
# background (log, 3 files with hashes = C6's, predictions, TEST_INSTRUCTIONS.md with every screen and the online
# warning) and the retoc path is saved to umgeditor.json; then canary C8's timer edit (Time_Timer Left 491, pushed out of
# Normal_Display): its Danger warning is listed and Build stays disabled until it is ticked (not built). Creates project $Project and work\out\mods\$Project-mod
# (delete both, and umgeditor.json next to the exe if it wasn't there before). Stops at the first failed check.
# usage: powershell -NoProfile -ExecutionPolicy Bypass -File drive6g.ps1 -Exe <UmgEditor.App.exe> -Out <folder> -FakeRetoc <retoc_failverify.cmd>
param([string]$Exe, [string]$Out, [string]$Project = 'zz6g', [string]$FakeRetoc)
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

# ---- 6g ----
$uia2 = [System.Windows.Automation.AutomationElement].Assembly.Location
$uiat2 = [System.Windows.Automation.ControlType].Assembly.Location
Add-Type -ReferencedAssemblies $uia2, $uiat2 -TypeDefinition @"
using System.Threading.Tasks; using System.Windows.Automation;
public static class LaterInvoke {
    // Invoke() on a button that opens a modal window blocks until it closes, so invoke from another thread
    public static void Invoke(AutomationElement e) { Task.Run(() => { try { ((InvokePattern) e.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); } catch { } }); }
}
"@
function ShotOf($el, $name) {
    Start-Sleep -Milliseconds 700
    $r = $el.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
    $bmp.Save("$Out\$name.png"); $g.Dispose(); $bmp.Dispose()
    "shot $name"
}
# the export dialog: a top-level window of the app titled "Export mod — project …"
function OpenExport {
    [LaterInvoke]::Invoke((ById $win 'ExportButton'))
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 20) {
        # an owned window is a child of its owner in UIA (or of the desktop)
        $d = @($win, $AE::RootElement) | ForEach-Object { $_.FindAll($TS::Children, (Cond $AE::ControlTypeProperty $CT::Window)) } |
            Where-Object { $_.Current.Name -like 'Export mod*' } | Select-Object -First 1
        if ($d) { Start-Sleep -Seconds 1; return $d }
        Start-Sleep -Milliseconds 300
    }
    throw "export dialog not shown"
}
function DText($id) { $e = $script:dlg.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty $id)); if ($e) { $e.Current.Name } else { $null } }
function DialogEl($id) { ById $script:dlg $id 10 }
function DSet($id, $text) { SetText (DialogEl $id) $text; Start-Sleep -Milliseconds 700 }
function Toggled($id) { (DialogEl $id).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState }
function WaitResult($like, $timeout = 120) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeout) { $t = DText 'ExportResultText'; if ($t -like $like) { return $t }; Start-Sleep -Milliseconds 700 }
    throw "result not '$like': $(DText 'ExportResultText')"
}
function CloseExport { Invoke (DialogEl 'ExportCloseButton'); Start-Sleep -Seconds 1 }
function Sha($path) { (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() }

$repo = 'E:\n3tron\ai_tools\scoreboard'
$realRetoc = 'E:\n3tron\patch\Tools\retoc-x86_64-pc-windows-msvc\retoc.exe'
$mod = "$Project-mod"
$modDir = "$repo\work\out\mods\$mod"
$c6 = @{ 'pc9999_mobile_and_P.pak' = '0c9d79129f5ca913335ca2f864ae40d39818a16a12805c9ab55fa1329c7f9424'; 'pc9999_mobile_and_P.ucas' = 'cdb2945a84c73359118fe9b8f51418364c9ecf35a2ac95529bba661d4bb8546d'; 'pc9999_mobile_and_P.utoc' = '56ad50b6ce2183964a35f9c1b8571c3fb70e945d687fbec3f05d1a24a089409b' }
if (Test-Path $modDir) { throw "$modDir exists; remove it first" }

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

    # 0) canary C6's two edits: the shared plate saved, the score box left unsaved
    Select-Package 'MatchTime/MatchTimeBase' 'MatchTimeBase.uasset'
    Select-Tree @('Img_Plate') | Out-Null
    TypeEnter (Field 'Offsets.Right') '100'
    Answer (Dialog 'First edit of MatchTimeBase') 'OK'
    Start-Sleep -Seconds 2
    Keys '^s'
    Select-Package 'MatchTime/MatchTime' '/MatchTime/MatchTime.uasset'
    Select-Tree @('ScaleBox_0', 'Main', 'Window', 'Page_1', 'MatchTime', 'Normal', 'Normal_Display', 'Img_MatchTimeBg_1') | Out-Null
    TypeEnter (Field 'Offsets.Left') '146'
    Answer (Dialog 'First edit of MatchTime') 'OK'
    Start-Sleep -Seconds 2
    "title: $(Title)"

    # 1) unsaved edits: refused, Build disabled, the dirty package unticked
    $script:dlg = OpenExport
    "dialog: $($script:dlg.Current.Name)"
    "refusals: $(DText 'ExportRefusals')"
    Check 'refused while MatchTime is unsaved' ((DText 'ExportRefusals') -like '*unsaved edits in MatchTime*Save (or undo) them first*')
    Check 'Build disabled' (-not (DialogEl 'ExportBuildButton').Current.IsEnabled)
    Check 'MatchTime (unsaved) unticked, MatchTimeBase ticked' ((Toggled 'ExportPackage_MatchTime') -eq 'Off' -and (Toggled 'ExportPackage_MatchTimeBase') -eq 'On')
    ShotOf $script:dlg '1_refused_unsaved'
    CloseExport

    # 2) saved: no refusal; the retoc hash shown (the tested build); a container without _P is refused
    Keys '^s'
    $script:dlg = OpenExport
    Check 'both packages saved and ticked' ((Toggled 'ExportPackage_MatchTime') -eq 'On' -and (Toggled 'ExportPackage_MatchTimeBase') -eq 'On')
    "retoc: $(DText 'ExportRetocInfo')"
    Check 'retoc hash shown: the tested build' ((DText 'ExportRetocInfo') -like 'sha256 1587f00d57c5046dc51dbc5c0e39f65d937a57943cac1db4ce059335d3a1e3a4 (the tested build*')
    DSet 'ExportModName' $mod
    "out dir: $(DText 'ExportOutDir')"
    Check 'output under work\out\mods\<name>' ((DText 'ExportOutDir') -like "$modDir *")
    Check 'nothing refused' ($null -eq (DText 'ExportRefusals') -and (DialogEl 'ExportBuildButton').Current.IsEnabled)
    Check 'C6: no layout warnings' ((DText 'ExportImpactWarnings') -eq 'No layout warnings for the picked edits.')
    DSet 'ExportContainer' 'pc9999_mobile_and'
    "refusals: $(DText 'ExportRefusals')"
    Check 'container must end in _P' ((DText 'ExportRefusals') -like '*ending in _P*' -and -not (DialogEl 'ExportBuildButton').Current.IsEnabled)
    ShotOf $script:dlg '2_refused_container'
    DSet 'ExportContainer' 'pc9999_mobile_and_P'
    DSet 'ExportModName' '..\escape'
    Check 'mod name must be a plain folder name' ((DText 'ExportRefusals') -like '*plain folder name*')
    DSet 'ExportModName' $mod

    # 3) a retoc whose verify fails: the build aborts and removes mod\
    DSet 'ExportRetocPath' 'E:\nowhere\retoc.exe'
    Check 'missing retoc refused' ((DText 'ExportRefusals') -like '*retoc not found*')
    DSet 'ExportRetocPath' $FakeRetoc
    "retoc (fake): $(DText 'ExportRetocInfo') / warnings: $(DText 'ExportWarnings')"
    Check 'a different retoc is a warning, not a refusal' ((DText 'ExportWarnings') -like '*not the tested build*' -and (DialogEl 'ExportBuildButton').Current.IsEnabled)
    Invoke (DialogEl 'ExportBuildButton')
    $r = WaitResult 'Build failed*'
    "result: $r"
    Check 'failed verify reported' ($r -like '*retoc verify failed*')
    Check 'mod\ removed, FAILED.txt written' (-not (Test-Path "$modDir\mod") -and (Test-Path "$modDir\FAILED.txt"))
    Check 'no instructions for a failed build' (-not (Test-Path "$modDir\TEST_INSTRUCTIONS.md"))
    ShotOf $script:dlg '3_failed_verify'

    # 4) the real retoc: built in the background, log, file list, hashes (= canary C6, proven in game)
    DSet 'ExportRetocPath' $realRetoc
    Check 'existing folder: a warning' ((DText 'ExportWarnings') -like '*exists and will be replaced*')
    Invoke (DialogEl 'ExportBuildButton')
    Start-Sleep -Milliseconds 800
    "while building: $(DText 'ExportResultText')"
    $r = WaitResult 'Built*'
    "result: $r"
    $log = (DialogEl 'ExportLog').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    "--- log (last lines):"; ($log -split "`n" | Where-Object { $_ } | Select-Object -Last 9) -join "`n"
    Check 'log shows pack-raw, verify and the end' ($log -like '*retoc pack-raw*' -and $log -like '*retoc verify*' -and $log -like '*Remove the mod before playing online*')
    $rows = (DialogEl 'ExportFiles').FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::DataItem))
    "file rows: $($rows.Count)"
    Check 'three files listed' ($rows.Count -eq 3)
    foreach ($f in $c6.Keys) {
        Check "$f on disk with the listed hash = canary C6" ((Sha "$modDir\mod\$f") -eq $c6[$f] -and $log -like "*$($c6[$f])*")
    }
    Check 'only the device files in mod\' (@(Get-ChildItem "$modDir\mod").Count -eq 3)
    $md = Get-Content "$modDir\TEST_INSTRUCTIONS.md" -Raw -Encoding UTF8
    Check 'instructions: the online warning' ($md -like '*Remove the mod before playing online*')
    foreach ($s in 'MatchTime', 'MatchTimeCard', 'MatchTimeGoal', 'MatchTimeMemberChange', 'MatchTimeConnection', 'MatchTimeConnectionCard', 'MatchTimeConnectionGoal', 'MatchTimeConnectionMemberChange') {
        Check "instructions list the screen $s" ($md -like "*| $s |*")
    }
    Check '18 prediction files' (@(Get-ChildItem "$modDir\predictions" -Filter *.json).Count -eq 18)
    Check 'build.json records the retoc hash' ((Get-Content "$modDir\build.json" -Raw) -like '*1587f00d57c5046dc51dbc5c0e39f65d937a57943cac1db4ce059335d3a1e3a4*')
    ShotOf $script:dlg '4_built'
    CloseExport
    Check 'the retoc path was saved as a setting' ((Get-Content (Join-Path (Split-Path $Exe) 'umgeditor.json') -Raw) -like '*retoc-x86_64-pc-windows-msvc\\retoc.exe*')
    "status line: $(ActionStatus)"

    # 5) canary C8's timer edit saved too: the Impact panel's Danger warning is listed and Build waits for its tick
    Select-Tree @('ScaleBox_0', 'Main', 'Window', 'Page_1', 'MatchTime', 'Normal', 'Normal_Display', 'Time_Timer') | Out-Null
    TypeEnter (Field 'Offsets.Left') '491'
    Keys '^s'
    $script:dlg = OpenExport
    $iw = (DialogEl 'ExportImpactWarningsHeader').Current.HelpText
    "impact warnings: $iw"
    "refusals: $(DText 'ExportRefusals')"
    Check 'the timer Danger warning is listed' ($iw -like '*`[Danger`] MatchTime: Time_Timer ends up almost completely outside Normal_Display*`[tick to acknowledge`]*')
    Check 'warning row shown' ((DText 'ExportImpactWarning_0') -like '`[Danger`] MatchTime: Time_Timer*')
    Check 'refused until ticked, Build disabled' ((DText 'ExportRefusals') -like '*A danger warning is not acknowledged (tick it below*' -and -not (DialogEl 'ExportBuildButton').Current.IsEnabled)
    Check 'tick box unticked' ((Toggled 'ExportAck_0') -eq 'Off')
    ShotOf $script:dlg '5_danger_unticked'
    (DialogEl 'ExportAck_0').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 700
    Check 'ticked: nothing refused, Build enabled' ($null -eq (DText 'ExportRefusals') -and (DialogEl 'ExportBuildButton').Current.IsEnabled)
    Check 'ticked state shown in the summary' ((DialogEl 'ExportImpactWarningsHeader').Current.HelpText -like '*`[acknowledged`]*')
    ShotOf $script:dlg '5_danger_ticked'
    (DialogEl 'ExportAck_0').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 700
    Check 'unticked again: Build disabled' (-not (DialogEl 'ExportBuildButton').Current.IsEnabled)
    CloseExport # not built: the ticked build is covered by ExportTests.Run_TimerOut_*

    [void]$p.CloseMainWindow()
    Start-Sleep -Seconds 3
    Check 'closed' $p.HasExited
}
finally {
    if (-not $p.HasExited) { try { Shot 'zz_last' } catch {}; $p.Kill() }
}
"done"
