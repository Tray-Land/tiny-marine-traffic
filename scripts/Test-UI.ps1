param(
    [Parameter(Mandatory)][int]$AppPid,
    [ValidateSet('local', 'sandbox')][string]$Target = 'local',
    [Parameter(Mandatory)][ValidateSet('local', 'sandbox')][string]$AppScope,
    [Parameter(Mandatory)][string]$ArtifactDirectory
)

$ErrorActionPreference = 'Stop'
if ($AppScope -ne $Target -or $AppPid -le 0) { throw 'Use a positive PID captured from the same execution target.' }
$scopeArgs = @(if ($Target -eq 'sandbox') { '--on'; 'sandbox' })
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null

function Ui([string[]]$Arguments, [switch]$AcrossWindows) {
    $selector = if ($AcrossWindows -or -not $script:hwnd) { @('-a', "$AppPid") } else { @('-w', $script:hwnd) }
    $output = @(& winapp ui @Arguments @scopeArgs @selector)
    if ($LASTEXITCODE -ne 0) {
        throw "winapp ui $($Arguments -join ' ') failed: $($output -join "`n")"
    }
    $output
}
function Test-UI([string]$Name, [scriptblock]$Test) {
    try {
        & $Test | Out-Null
        $results.Add(@{ name = $Name; status = 'PASS' })
    }
    catch {
        $results.Add(@{ name = $Name; status = 'FAIL'; detail = "$_" })
    }
}
function Nodes($items) {
    foreach ($item in $items) {
        $item
        if ($item.children) { Nodes $item.children }
    }
}
function Capture([string]$Name) {
    $path = Join-Path $ArtifactDirectory $Name
    if (Test-Path -LiteralPath $path) { throw "Choose a new evidence directory: $path" }
    Ui @('screenshot', '--output', $path) | Out-Null
    if (-not (Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "No screenshot was delivered: $path"
    }
    $screenshots.Add($path)
}
function Select-Picker([string]$Picker, [string]$Name) {
    if ($Picker -eq 'HarborPicker') {
        Open-Places
    }
    Ui @('invoke', $Picker, '--action', 'expand') | Out-Null
    $tree = Ui @('inspect', '--json', '-d', '10') | ConvertFrom-Json
    $item = Nodes $tree.windows.elements | Where-Object {
        $_.type -eq 'ListItem' -and $_.name -eq $Name
    } | Select-Object -First 1
    Assert ($null -ne $item.selector) "The dropdown did not expose an item named '$Name'."
    Ui @('invoke', $item.selector, '--action', 'select') | Out-Null
    if ($Picker -eq 'HarborPicker') {
        Assert-CurrentPlace $Name
        Assert-PlacesHidden
    }
    else {
        Ui @('wait-for', $Picker, '--value', $Name) | Out-Null
    }
}
function Open-Places {
    Ui @('invoke', 'ChangePlaceButton') | Out-Null
    Ui @('wait-for', 'HarborPicker') | Out-Null
    Ui @('wait-for', 'SaveAreaButton') | Out-Null
}
function Assert-PlacesHidden {
    Ui @('wait-for', 'HarborPicker', '--gone', '-t', '3000') | Out-Null
    Ui @('wait-for', 'SaveAreaButton', '--gone', '-t', '3000') | Out-Null
}
function Assert-CurrentPlace([string]$Name) {
    Ui @('wait-for', 'ChangePlaceButton', '-p', 'Name', '--value', "Change place ($Name)") | Out-Null
}
function Read-Catalog {
    $read = @'
$ErrorActionPreference = 'Stop'
Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'Packages\93253F2C-1787-4C0A-8E3C-C3EEE6B56488_vek3qaanxbakt\LocalState\areas.json') -Raw
'@
    if ($Target -eq 'sandbox') {
        $json = & winapp target exec sandbox -- powershell.exe -NoProfile -NonInteractive -Command $read
        if ($LASTEXITCODE -ne 0) { throw 'Could not read saved areas in the selected target.' }
    }
    else {
        $json = & ([scriptblock]::Create($read))
    }
    $json | ConvertFrom-Json
}
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Audit-Controls {
    $tree = Ui @('inspect', '-i', '--json') | ConvertFrom-Json
    $controls = @(Nodes $tree.windows.elements | Where-Object {
        $_.type -match 'Button|ComboBox|Edit|CheckBox' -and $_.name -ne 'Close'
    })
    Assert ($controls.Count -ge 6) 'No meaningful application controls were audited.'
    $bad = @($controls | Where-Object { -not $_.name -or -not $_.automationId })
    Assert ($bad.Count -eq 0) "Missing names or IDs: $(($bad | ForEach-Object name) -join ', ')"
}

try {
    $windows = @(Ui @('list-windows', '--json') -AcrossWindows | ConvertFrom-Json)
    $main = $windows | Where-Object title -eq 'Tiny Marine Traffic' | Select-Object -First 1
    if (-not $main) { throw 'Open the traffic flyout before running this batch.' }
    $script:hwnd = [string]$main.hwnd
    $homeProperty = Ui @('get-property', 'HomeButton', '--json') | ConvertFrom-Json
    Assert ($homeProperty.element.name -match '^Go to home area \((.+)\)$') 'No named home area was exposed.'
    $originalHome = $Matches[1]
    $fixture = "UI home $([guid]::NewGuid().ToString('N').Substring(0, 6))"
    $renamed = "$fixture edited"
    $extra = "$fixture extra"
    Test-UI 'Place controls are hidden until requested' {
        Ui @('wait-for', 'ChangePlaceButton')
        Assert-PlacesHidden
        Capture 'traffic-default.png'
        Open-Places
        Audit-Controls
        Capture 'change-place.png'
        Ui @('send-keys', 'esc', '--via', 'send-input')
        Assert-PlacesHidden
        Ui @('wait-for', 'ChangePlaceButton')
        Ui @('wait-for', 'VesselList')
    }
    Test-UI 'Clicking the map dismisses the place picker' {
        Open-Places
        Ui @('click', 'TrafficMap')
        Assert-PlacesHidden
        Ui @('wait-for', 'ChangePlaceButton')
    }
    Test-UI 'Default harbor and live connection' {
        Select-Picker 'HarborPicker' 'Bergen'
        Select-Picker 'HarborPicker' 'Oslofjord'
        Assert-CurrentPlace 'Oslofjord'
        Ui @('wait-for', 'MovingToggle', '--value', 'On')
        Ui @('wait-for', 'ConnectionText', '--value', 'Live', '--contains', '-t', '25000')
        $count = Ui @('get-value', 'CountText', '--json') | ConvertFrom-Json
        if ($count.text -notmatch '^\d+' -or [int]$Matches[0] -eq 0) { throw 'No real vessel reports were displayed.' }
        Capture 'traffic.png'
    }
    $before = Read-Catalog
    Test-UI 'Selectable vessel details' {
        $tree = Ui @('inspect', '--json', '-d', '9') | ConvertFrom-Json
        $row = Nodes $tree.windows.elements | Where-Object { $_.automationId -like 'Vessel-*' } | Select-Object -First 1
        if (-not $row) { throw 'No accessible vessel row was realized.' }
        if ($row.name -like '*VesselRow*') { throw 'The vessel accessible name is a CLR type name.' }
        Ui @('invoke', $row.automationId)
        Ui @('wait-for', 'VesselName')
        Capture 'vessel.png'
        Ui @('invoke', 'CloseDetailButton')
        Ui @('wait-for', 'VesselList')
    }
    Test-UI 'Moving-only filter' {
        Ui @('invoke', 'MovingToggle', '--action', 'toggle-off')
        Ui @('wait-for', 'MovingToggle', '--value', 'Off')
        Capture 'traffic-all.png'
        Ui @('invoke', 'MovingToggle', '--action', 'toggle-on')
        Ui @('wait-for', 'MovingToggle', '--value', 'On')
    }
    Test-UI 'Pick a map spot and save it as home' {
        Open-Places
        Ui @('invoke', 'SaveAreaButton')
        Assert-PlacesHidden
        Ui @('wait-for', 'MakeHomeCheck', '--value', 'On')
        $initial = Ui @('get-value', 'AreaPreviewText', '--json') | ConvertFrom-Json
        $map = Ui @('get-property', 'TrafficMap', '-p', 'BoundingRectangle', '--json') | ConvertFrom-Json
        $x = [int]($map.element.x + $map.element.width * 0.68)
        $y = [int]($map.element.y + $map.element.height * 0.43)
        Ui @('touch', 'TrafficMap', '--at', "$x,$y")
        Start-Sleep -Milliseconds 300
        $picked = Ui @('get-value', 'AreaPreviewText', '--json') | ConvertFrom-Json
        Assert ($picked.text -ne $initial.text) 'Tapping a map spot did not move the chosen center.'
        Ui @('click', 'TrafficMap', '--double')
        Start-Sleep -Milliseconds 500
        $zoomed = Ui @('get-value', 'AreaPreviewText', '--json') | ConvertFrom-Json
        Assert ($zoomed.text -ne $picked.text) 'Zooming did not update the chosen area.'
        $script:pickedPreview = $zoomed.text
        Ui @('set-value', 'PickedAreaNameBox', $fixture)
        Audit-Controls
        Capture 'pick-area.png'
        Ui @('invoke', 'ConfirmAreaButton')
        Assert-CurrentPlace $fixture
        Assert-PlacesHidden
        $saved = Read-Catalog
        Assert ($saved.HomeName -eq $fixture -and $saved.SelectedName -eq $fixture) 'Home/selection was not saved.'
        Assert ($saved.Areas.Count -eq $before.Areas.Count + 1) 'The new area was not added to the saved list.'
        Assert ($null -ne ($saved.Areas | Where-Object Name -eq $fixture).ZoomLevel) 'The map zoom was not persisted.'
    }
    Test-UI 'Home restores the saved map center and zoom' {
        Select-Picker 'HarborPicker' 'Bergen'
        Ui @('invoke', 'HomeButton')
        Assert-CurrentPlace $fixture
        Open-Places
        Ui @('invoke', 'SaveAreaButton')
        Ui @('wait-for', 'AreaPreviewText', '--value', $script:pickedPreview)
        Ui @('invoke', 'CancelPickAreaButton')
        Assert ((Read-Catalog).Areas.Count -eq $before.Areas.Count + 1) 'Cancel unexpectedly saved another area.'
    }
    Test-UI 'Rename home and edit coordinates and range in Settings' {
        Ui @('invoke', 'SettingsButton')
        Ui @('wait-for', 'SavedAreaPicker', '--value', $fixture)
        Ui @('set-value', 'AreaNameBox', $renamed)
        Ui @('set-value', 'Latitude Minimum-80 Maximum80', '59.83')
        Ui @('set-value', 'Longitude Minimum-180 Maximum180', '10.66')
        Ui @('set-value', 'Area half-width (nautical miles) Minimum2 Maximum50', '10')
        Ui @('focus', 'BackButton')
        Ui @('invoke', 'SaveAreaChangesButton')
        Ui @('wait-for', 'SavedAreaPicker', '--value', $renamed)
        Ui @('wait-for', 'HomeAreaText', '--value', "Home: $renamed")
        Ui @('wait-for', 'InputBox', '--root', 'RadiusBox', '--type', 'Edit', '--value', '10')
        Audit-Controls
        Capture 'settings.png'
        Ui @('invoke', 'BackButton')
        Assert-CurrentPlace $renamed
        Assert-PlacesHidden
        Ui @('wait-for', 'ConnectionText', '--value', '10 nm', '--contains', '-t', '25000')
        Ui @('invoke', 'SettingsButton')
        Ui @('wait-for', 'AreaNameBox', '--value', $renamed)
        Ui @('wait-for', 'InputBox', '--root', 'LatitudeBox', '--type', 'Edit', '--value', '59.83')
        Ui @('wait-for', 'InputBox', '--root', 'LongitudeBox', '--type', 'Edit', '--value', '10.66')
        Ui @('wait-for', 'InputBox', '--root', 'RadiusBox', '--type', 'Edit', '--value', '10')
    }
    Test-UI 'Add a dropdown area in Settings and reject duplicate names' {
        Ui @('invoke', 'AddAreaButton')
        Ui @('set-value', 'AreaNameBox', $extra)
        Ui @('invoke', 'SaveAreaChangesButton')
        Ui @('wait-for', 'SavedAreaPicker', '--value', $extra)
        Ui @('set-value', 'AreaNameBox', 'Oslofjord')
        Ui @('invoke', 'SaveAreaChangesButton')
        Ui @('wait-for', 'Message', '--type', 'Text', '--value', 'already exists', '--contains')
        Assert ((Read-Catalog).Areas.Count -eq $before.Areas.Count + 2) 'Invalid edits changed the saved list.'
        Ui @('set-value', 'AreaNameBox', $extra)
        Ui @('invoke', 'BackButton')
        Assert-CurrentPlace $extra
        Ui @('invoke', 'SettingsButton')
    }
    Test-UI 'Deleting an area requires confirmation' {
        Ui @('invoke', 'DeleteAreaButton')
        Ui @('wait-for', 'Primary')
        Capture 'delete-area.png'
        Ui @('send-keys', 'esc', '--via', 'send-input')
        Ui @('wait-for', 'Primary', '--gone')
        Assert (@((Read-Catalog).Areas | Where-Object Name -eq $extra).Count -eq 1) 'Cancel deleted the area.'
        Ui @('invoke', 'DeleteAreaButton')
        Ui @('wait-for', 'Primary')
        Ui @('invoke', 'Primary')
        Ui @('wait-for', 'Primary', '--gone')
        Assert (@((Read-Catalog).Areas | Where-Object Name -eq $extra).Count -eq 0) 'Delete did not persist.'
        Ui @('wait-for', 'SavedAreaPicker', '--value', $renamed)
    }
    Test-UI 'Delete home, restore original home, and remove test areas' {
        Ui @('invoke', 'DeleteAreaButton')
        Ui @('wait-for', 'Primary')
        Ui @('invoke', 'Primary')
        Ui @('wait-for', 'Primary', '--gone')
        Select-Picker 'SavedAreaPicker' $originalHome
        if ((Read-Catalog).HomeName -ne $originalHome) {
            Ui @('invoke', 'MakeHomeButton')
        }
        Ui @('wait-for', 'HomeAreaText', '--value', "Home: $originalHome")
        $saved = Read-Catalog
        Assert ($saved.HomeName -eq $originalHome) 'The original home was not restored.'
        Assert ($saved.Areas.Count -eq $before.Areas.Count) 'Temporary areas remain in the saved list.'
        Assert (@($saved.Areas | Where-Object Name -in @($fixture, $renamed, $extra)).Count -eq 0) 'Test areas remain.'
        Ui @('invoke', 'BackButton')
        Ui @('invoke', 'HomeButton')
        Assert-CurrentPlace $originalHome
    }
    Test-UI 'Escape returns from settings without hiding' {
        Ui @('invoke', 'SettingsButton')
        Ui @('focus', 'BackButton')
        Ui @('send-keys', 'esc', '--via', 'send-input')
        Ui @('wait-for', 'ChangePlaceButton')
        Assert-PlacesHidden
    }
    Test-UI 'Restore Oslofjord preset' {
        Select-Picker 'HarborPicker' 'Oslofjord'
        Assert-CurrentPlace 'Oslofjord'
        Ui @('wait-for', 'ConnectionText', '--value', 'Live', '--contains', '-t', '25000')
    }
    Test-UI 'Interactive controls have accessible names and IDs' {
        Audit-Controls
    }
}
catch {
    $results.Add(@{ name = 'Test execution'; status = 'FAIL'; detail = "$_" })
}
finally {
    try {
        & winapp ui yield @scopeArgs | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not yield the UI workflow.' }
    }
    catch {
        $results.Add(@{ name = 'Release UI workflow'; status = 'FAIL'; detail = "$_" })
    }
    $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow
}
$failed = @($results | Where-Object status -eq 'FAIL').Count
$report = @{
    target = $Target; appPid = $AppPid; passed = $results.Count - $failed; failed = $failed
    results = $results.ToArray(); screenshots = $screenshots.ToArray()
}
$report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ArtifactDirectory 'results.json')
$report | ConvertTo-Json -Depth 8
if ($failed) { exit 1 }
