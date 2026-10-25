# Canary C18 (step 3 fix, 2026-10-25), made in the app the way a user makes it: project C18, two texture replacements
# with scope "This widget" on long-path ETC2 widgets that no same-length source fits, so both take the new route
# (path appended to the name map + the widget's 8-byte texture reference pointed at it):
#   MatchMemberChangeSpecial > Img_PlateBlue (8x8 ETC2, 154-character path; Img_PlateSkill1-3 name the same texture
#     and must stay stock) -> a solid green bar;
#   VirtualPadButtonLarge > Img_ButtonLargeFeedback (200x200 ETC2, 133 characters; the touch pad's big button, shown
#     while it is held; BtnConfigButtonLarge names it too and keeps it) -> a magenta disc with a yellow bar.
# Both clones come from ExtraLoginBonusInfo, the source C16/C16b_test already loaded on the device, so the pointed
# reference is the only new thing. Then the export dialog builds work\out\mods\C18 with the tested retoc.
# Creates project C18 (work\edits\C18) and work\out\mods\C18 and keeps them (they are the canary).
# usage: powershell -NoProfile -ExecutionPolicy Bypass -File driveC18.ps1 -Exe <UmgEditor.App.exe> -Out <folder for PNGs>
param([string]$Exe, [string]$Out, [string]$Project = 'C18', [string]$Mod = 'C18')
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

function Toggle($e) { $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Seconds 2 }
function Present($id) { [bool]$win.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty $id)) }
function Text($id) { $e = $win.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty $id)); if ($e) { $e.Current.Name } else { $null } }

# ---- step 3 ----
$uia2 = [System.Windows.Automation.AutomationElement].Assembly.Location
$uiat2 = [System.Windows.Automation.ControlType].Assembly.Location
Add-Type -ReferencedAssemblies $uia2, $uiat2 -TypeDefinition @"
using System.Threading.Tasks; using System.Windows.Automation;
public static class LaterInvoke {
    // Invoke() on a button that opens a modal window blocks until it closes, so invoke from another thread
    public static void Invoke(AutomationElement e) { Task.Run(() => { try { ((InvokePattern) e.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); } catch { } }); }
}
"@
# the common file dialog (#32770 with the given title): type the path into its file-name box and press Enter
function PickFile($caption, $path) {
    $h = [IntPtr]::Zero
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($h -eq [IntPtr]::Zero -and $sw.Elapsed.TotalSeconds -lt 20) { Start-Sleep -Milliseconds 300; $h = [W]::FindWindow('#32770', $caption) }
    if ($h -eq [IntPtr]::Zero) { throw "file dialog not shown: $caption" }
    Start-Sleep -Seconds 1
    [W]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 400
    [System.Windows.Forms.SendKeys]::SendWait($path)
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
    Start-Sleep -Seconds 2
}
function WaitText($id, $like, $timeout = 60) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeout) { $t = Text $id; if ($t -like $like) { return $t }; Start-Sleep -Milliseconds 500 }
    throw "$id not '$like': $(Text $id)"
}
# pick an item of a (virtualized) combo box by its name
function PickCombo($combo, $name) {
    Expand $combo; Start-Sleep -Milliseconds 800
    $ic = $combo.GetCurrentPattern([System.Windows.Automation.ItemContainerPattern]::Pattern)
    $item = $ic.FindItemByProperty($null, $AE::NameProperty, $name)
    if (-not $item) { throw "combo item not found: $name" }
    try { $item.GetCurrentPattern([System.Windows.Automation.VirtualizedItemPattern]::Pattern).Realize() } catch {}
    Start-Sleep -Milliseconds 500
    Select-Item $item; Start-Sleep -Milliseconds 800
    try { $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch {}
    Start-Sleep -Seconds 1
}
function IsChecked($e) { $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected }
function OpenExport {
    [LaterInvoke]::Invoke((ById $win 'ExportButton'))
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 20) {
        $d = @($win, $AE::RootElement) | ForEach-Object { $_.FindAll($TS::Children, (Cond $AE::ControlTypeProperty $CT::Window)) } |
            Where-Object { $_.Current.Name -like 'Export mod*' } | Select-Object -First 1
        if ($d) { Start-Sleep -Seconds 2; return $d }
        Start-Sleep -Milliseconds 300
    }
    throw "export dialog not shown"
}

function DText($id) { $e = $script:dlg.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty $id)); if ($e) { $e.Current.Name } else { $null } }
function DialogEl($id) { ById $script:dlg $id 10 }
function DSet($id, $text) { SetText (DialogEl $id) $text; Start-Sleep -Milliseconds 700 }
function Toggled($id) { (DialogEl $id).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState }
function WaitResult($like, $timeout = 300) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeout) { $t = DText 'ExportResultText'; if ($t -like $like) { return $t }; Start-Sleep -Milliseconds 700 }
    throw "result not '$like': $(DText 'ExportResultText')"
}
function ShotOf($el, $name) {
    Start-Sleep -Milliseconds 700
    $r = $el.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
    $bmp.Save("$Out\$name.png"); $g.Dispose(); $bmp.Dispose()
    "shot $name"
}

$repo = 'E:\n3tron\ai_tools\scoreboard'
$realRetoc = 'E:\n3tron\patch\Tools\retoc-x86_64-pc-windows-msvc\retoc.exe'
$modDir = "$repo\work\out\mods\$Mod"
$source = '/Game/Assets/ui/Data/Widget/General/Modals/ExtraLoginBonus/Tex720/ExtraLoginBonusInfo'
if (Test-Path $modDir) { throw "$modDir exists; remove it first" }
if (Test-Path "$repo\work\edits\$Project") { throw "project $Project exists; remove it first" }

# the art (made once, kept in work\out\art\C18): a solid green bar for the plate; for the pad's feedback disc the stock's
# 200x200 size with a magenta disc and a yellow bar across it on transparent (the stock is a plain grey disc)
$art = "$repo\work\out\art\C18"
New-Item -ItemType Directory -Force $art | Out-Null
$plate = "$art\C18_PlateBlue_64x16.png"
$disc = "$art\C18_ButtonLargeFeedback_200x200.png"
if (-not (Test-Path $plate)) {
    $bmp = New-Object System.Drawing.Bitmap(64, 16, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::FromArgb(255, 0, 200, 80)); $g.Dispose()
    $bmp.Save($plate, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
if (-not (Test-Path $disc)) {
    $bmp = New-Object System.Drawing.Bitmap(200, 200, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
    $g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 230, 0, 180))), 20, 20, 160, 160)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 220, 0))), 30, 88, 140, 24)
    $g.Dispose(); $bmp.Save($disc, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
"art: $((Get-FileHash $plate -Algorithm SHA256).Hash.ToLowerInvariant())  $plate"
"art: $((Get-FileHash $disc -Algorithm SHA256).Hash.ToLowerInvariant())  $disc"

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

# one replacement: select the widget, This widget, pick the source, import, check the trial, inject (first edit asks)
function Replace($search, $file, [string[]]$tree, $widget, $texture, $png, $firstEdit) {
    Select-Package $search $file
    Tab (TreeTab)  # an inject leaves the Preview tab selected, and the tree only exists on its own tab
    Select-Tree $tree | Out-Null
    WaitText 'TextureNameText' $texture | Out-Null
    "  $widget shows $(Text 'TextureNameText') ($(Text 'TextureSizeText')); $(Text 'TextureUsersHeader')"
    Select-Item (ById $win 'TrialScopeWidget'); Start-Sleep -Milliseconds 800
    Check "$widget`: This widget ticked" (IsChecked (ById $win 'TrialScopeWidget'))
    PickCombo (ById $win 'TrialSourcePicker') $source
    Shot "${widget}_1_section"
    [LaterInvoke]::Invoke((ById $win 'ImportTextureButton'))
    PickFile "Image for $widget (replaces $texture)" $png
    $check = WaitText 'TrialCheckText' 'OK:*' 120
    "  trial check: $($check -replace "`n", ' | ')"
    Check "$widget`: the trial is planned, pointed route" ($check -like '*appended to the name map*')
    Check "$widget`: cloned next to ExtraLoginBonusInfo" ($check -like '*New package: ExtraLoginBonusIn*')
    Shot "${widget}_2_trial"
    [LaterInvoke]::Invoke((ById $win 'InjectTextureButton'))
    $d = Dialog $firstEdit
    "  first-edit prompt: $(DialogText $d)"
    Answer $d 'OK'
    $edit = WaitText 'TextureEditText' 'Replaced in this project*' 30
    "  edit: $edit"
    Check "$widget`: this widget only, cloned from ExtraLoginBonusInfo" ($edit -like '*this widget only*cloned from ExtraLoginBonusInfo*')
}

try {
    SetText (ById $win 'NewProjectBox') $Project
    Start-Sleep -Milliseconds 500
    Invoke $create
    Start-Sleep -Seconds 2
    "title: $(Title)"

    Replace 'MatchMemberChangeSpecial' '/MatchMemberChangeSpecial.uasset' @('ScaleBox', 'Main', 'Window', 'Parts_MemberChange', 'Img_PlateBlue') `
        'Img_PlateBlue' 'MatchMemberChangeSpecialPlate_1' $plate 'First edit of MatchMemberChangeSpecial'
    Replace 'VirtualPadButtonLarge' '/VirtualPadButtonLarge.uasset' @('ButtonLarge', 'Img_ButtonLargeFeedback') `
        'Img_ButtonLargeFeedback' 'VirtualPadButtonLargeFeedback' $disc 'First edit of VirtualPadButtonLarge'

    Invoke (ById $win 'SaveButton'); Start-Sleep -Seconds 3
    Check 'saved: title clean' (-not ((Title) -like '*`*'))
    $json = Get-Content "$repo\work\edits\$Project\textures.json" -Raw
    Check 'textures.json: version 2, both Widget scope' ($json -like '*"Version": 2*' -and ([regex]::Matches($json, '"Scope": "Widget"')).Count -eq 2)
    Tab (ById $win 'BytesTab')
    $sw.Restart(); while (-not ((ById $win 'BytesTab').Current.Name -like '*2 textures*') -and $sw.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 500 }
    Shot 'bytes'

    # the export, as a user builds it: both packages ticked, the tested retoc, mod name C18
    $script:dlg = OpenExport
    DSet 'ExportModName' $Mod
    DSet 'ExportContainer' 'pc9999_mobile_and_P'
    DSet 'ExportRetocPath' $realRetoc
    "retoc: $(DText 'ExportRetocInfo')"
    Check 'the tested retoc' ((DText 'ExportRetocInfo') -like 'sha256 1587f00d57c5046dc51dbc5c0e39f65d937a57943cac1db4ce059335d3a1e3a4 (the tested build*')
    Check 'both re-pointed packages ticked' ((Toggled 'ExportPackage_MatchMemberChangeSpecial') -eq 'On' -and (Toggled 'ExportPackage_VirtualPadButtonLarge') -eq 'On')
    "textures: $(DText 'ExportTextures')"
    "refusals: $(DText 'ExportRefusals') / warnings: $(DText 'ExportWarnings')"
    Check 'nothing refused' ($null -eq (DText 'ExportRefusals') -and (DialogEl 'ExportBuildButton').Current.IsEnabled)
    ShotOf $script:dlg 'export_before'
    Invoke (DialogEl 'ExportBuildButton')
    $r = WaitResult 'Built*'
    "result: $r"
    $log = (DialogEl 'ExportLog').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    Set-Content -Path "$Out\export_log.txt" -Value $log -Encoding UTF8
    Check 'log: pack-raw, verify, every clone decodes' ($log -like '*retoc pack-raw*' -and $log -like '*retoc verify*' -and $log -like '*decode*')
    ShotOf $script:dlg 'export_built'
    Invoke (DialogEl 'ExportCloseButton'); Start-Sleep -Seconds 1
    foreach ($f in Get-ChildItem "$modDir\mod") { "  $((Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($f.Name)  $($f.Length) bytes" }
    "ALL CHECKS PASSED"
}
finally {
    try { $p.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2; if (-not $p.HasExited) { $p.Kill() } } catch {}
}
