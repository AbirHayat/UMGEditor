# Step 3 UI run (textures): click the scoreboard logo -> its texture, size, theme copies and every widget that draws it;
# import a PNG -> a trial in the Preview tab (banner, magenta outline, Stock toggle), checked in the background; a name that
# doesn't fit is refused; inject (first-edit prompt) -> project edit, Bytes tab row, undo/redo/save (textures.json); the
# export dialog lists it; "Open" on the online board's row opens it with its logo selected. Creates project $Project
# (work\edits\zztex; delete it afterwards). Stops at the first failed check.
# usage: powershell -NoProfile -ExecutionPolicy Bypass -File driveTex.ps1 -Exe <UmgEditor.App.exe> -Out <folder for PNGs>
param([string]$Exe, [string]$Out, [string]$Project = 'zztex')
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
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

# a test image: 80x80, a blue disc with a yellow ring on transparent (System.Drawing writes 8-bit RGBA PNG)
$png = Join-Path $Out 'trial_logo.png'
$bmp = New-Object System.Drawing.Bitmap(80, 80, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 30, 60, 200))), 4, 4, 72, 72)
$g.DrawEllipse((New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 250, 210, 0), 6)), 10, 10, 60, 60)
$g.Dispose(); $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

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

    # 1) click the scoreboard logo: its texture, who else draws it
    Select-Package 'MatchTime/MatchTime' '/MatchTime.uasset'
    Select-Tree @('ScaleBox_0', 'Main', 'Window', 'Page_1', 'MatchTime', 'Img_Logo') | Out-Null
    WaitText 'TextureNameText' 'MatchTimeLogo' | Out-Null
    "texture: $(Text 'TextureNameText') / $(Text 'TextureSizeText') / $(Text 'TextureUsersHeader')"
    Check 'texture section shows the logo texture' ((Text 'TextureSizeText') -like '80*80*ETC2_RGB*')
    Check 'used by 2 widgets' ((Text 'TextureUsersHeader') -eq 'Used by 2 widgets')
    Check 'the online board is listed with an Open button' (Present 'OpenUser_MatchTimeConnection_Img_Logo')
    Check 'no Open button for the widget itself' (-not (ById $win 'OpenUser_MatchTime_Img_Logo').Current.IsEnabled)
    Shot '1_texture_section'

    # 2) import a PNG: a trial in the Preview tab, checked in the background, nothing in the project yet
    [LaterInvoke]::Invoke((ById $win 'ImportTextureButton'))
    PickFile 'Image for Img_Logo (replaces MatchTimeLogo)' $png
    $check = WaitText 'TrialCheckText' 'OK:*' 90
    "trial check: $check"
    "trial banner: $(Text 'TrialBannerText')"
    Check 'trial banner shown' ((Text 'TrialBannerText') -like 'Trial (not in the project yet): trial_logo.png 80*80 on MatchTime*Img_Logo*')
    Check 'the check names the new package and the re-point' ($check -like '*New package: ExtraLoginBonus*Re-pointed: MatchTime.*')
    Check 'the online board keeps the stock logo (listed)' ($check -like '*MatchTimeConnection also names MatchTimeLogo and keeps the stock image*')
    $name = Value (ById $win 'TrialNameBox')
    Check "suggested name '$name' is as long as ExtraLoginBonusInfo" ($name.Length -eq 19)
    Check 'project still clean' (-not ((Title) -like '*`*'))
    Check 'preview tab selected' (IsSelectedTab (PreviewTab))
    Shot '2_trial_preview'
    Toggle (ById $win 'StockCheck')
    Check 'stock: the banner says the trial is not drawn' ((Text 'TrialBannerText') -like '*Stock is ticked*')
    Shot '3_trial_stock'
    Toggle (ById $win 'StockCheck')

    # 3) a name that doesn't fit is refused by the check; back to the suggestion
    SetText (ById $win 'TrialNameBox') 'TooShort'
    Front; (ById $win 'TrialMarginsCheck').SetFocus(); Start-Sleep -Seconds 1
    $bad = WaitText 'TrialCheckText' '*must be as long as*' 30
    "bad name: $bad"
    SetText (ById $win 'TrialNameBox') $name
    Front; (ById $win 'TrialMarginsCheck').SetFocus(); Start-Sleep -Seconds 1
    WaitText 'TrialCheckText' 'OK:*' 60 | Out-Null

    # 4) inject: the first edit of MatchTime asks first
    [LaterInvoke]::Invoke((ById $win 'InjectTextureButton'))
    $d = Dialog 'First edit of MatchTime'
    "first-edit prompt: $(DialogText $d)"
    Check 'the prompt lists the new package and the other user' ((DialogText $d) -like '*New texture package: Widget/General/Modals/ExtraLoginBonus/Tex720/*MatchTimeConnection also names MatchTimeLogo*')
    Answer $d 'OK'
    WaitText 'TextureEditText' 'Replaced in this project*' 30 | Out-Null
    "edit: $(Text 'TextureEditText')"
    Check 'banner gone after inject' (-not (Present 'TrialBanner') -or -not (ById $win 'TrialBanner').Current.Name)
    Check 'project dirty' ((Title) -like '*`*')
    Tab (ById $win 'BytesTab')
    $sw.Restart(); while (-not ((ById $win 'BytesTab').Current.Name -like '*1 texture*') -and $sw.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 500 }
    "bytes tab: $((ById $win 'BytesTab').Current.Name)"
    Check 'bytes tab counts the texture' ((ById $win 'BytesTab').Current.Name -like '*1 texture*')
    $rows = @((ById $win 'TextureBytesList').FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like '*Img_Logo*MatchTimeLogo 80*80*' })
    Check 'texture row listed' ($rows.Count -ge 1)
    Shot '4_bytes'

    # 5) undo / redo / save
    Invoke (ById $win 'UndoButton'); Start-Sleep -Seconds 2
    Check 'undo removes the edit' (-not (Text 'TextureEditText'))
    Check "status: $(ActionStatus)" ((ActionStatus) -like '*Undid a texture edit*')
    Invoke (ById $win 'RedoButton'); Start-Sleep -Seconds 2
    WaitText 'TextureEditText' 'Replaced in this project*' 20 | Out-Null
    Invoke (ById $win 'SaveButton'); Start-Sleep -Seconds 3
    Check 'saved: title clean' (-not ((Title) -like '*`*'))
    $json = "E:\n3tron\ai_tools\scoreboard\work\edits\$Project\textures.json"
    Check 'textures.json written' (Test-Path $json)

    # 6) the export dialog lists the texture and its new package
    $script:dlg = OpenExport
    $tex = ($script:dlg.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty 'ExportTextures'))).Current.Name
    "export textures: $tex"
    Check 'export lists the texture edit' ($tex -like '*MatchTime*Img_Logo: MatchTimeLogo*ExtraLoginBonus*80*80 (+1 new package; re-points MatchTime)*')
    $note = ($script:dlg.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty 'ExportPackage_MatchTime'))).Current.Name
    "package row: $note"
    Invoke (ById $script:dlg 'ExportCloseButton' 10); Start-Sleep -Seconds 1

    # 7) "Open" on the other user: the online board opens with its own Img_Logo selected
    Tab (TreeTab)
    Invoke (ById $win 'OpenUser_MatchTimeConnection_Img_Logo')
    Start-Sleep -Seconds 5
    "after Open: status '$(ActionStatus)' / title '$(Title)' / node '$(Text 'TextureNameText')' / tree '$(Text 'TreeStatusText')'"
    Shot '5a_after_open'
    WaitText 'TextureNameText' 'MatchTimeLogo' 30 | Out-Null
    $hdr = (ById $win 'TextureUsersHeader').Current.Name
    Check 'opened MatchTimeConnection with its logo selected' ((Title) -notlike '*`*' -and (Present 'OpenUser_MatchTime_Img_Logo') -and (ById $win 'OpenUser_MatchTime_Img_Logo').Current.IsEnabled)
    Check 'its own row has no edit' (-not (Text 'TextureEditText'))
    Shot '5_open_user'
    "ALL CHECKS PASSED"
}
finally {
    try { $p.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2; if (-not $p.HasExited) { $p.Kill() } } catch {}
}
