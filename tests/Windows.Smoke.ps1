# Run with Windows PowerShell 5.1 in STA after building the WPF app.
# Renders controls without invoking maintenance or checking/downloading updates.
param(
    [string]$ExecutablePath = "$PSScriptRoot\..\dist\OpenSweep.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\artifacts\windows-smoke"
)
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run this script with powershell.exe -STA.' }
$executable = (Resolve-Path $ExecutablePath).Path
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$output = (Resolve-Path $OutputDirectory).Path
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$assembly = [Reflection.Assembly]::LoadFrom($executable)

# Test the actual Windows trust boundary, not a mocked signature result.
$auth = $assembly.GetType('OpenSweep.Core.SelfUpdateAuthenticode', $true)
$verify = $auth.GetMethod('GetTrustedPublicKey', [Reflection.BindingFlags]'Static,NonPublic')
if ($null -eq $verify) { throw 'Signature verification method is missing.' }
function Get-TrustedKey([string]$path) {
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { return $verify.Invoke($null, [object[]]@($stream)) }
    finally { $stream.Dispose() }
}
if ((Get-AuthenticodeSignature $executable).Status -ne 'NotSigned') {
    throw 'This smoke test expects the unsigned CI build, before release signing.'
}
$unsignedRejected = $false
try { Get-TrustedKey $executable | Out-Null }
catch {
    $errorCause = $_.Exception.GetBaseException()
    if ($errorCause -is [IO.InvalidDataException]) { $unsignedRejected = $true }
    else { throw }
}
if (-not $unsignedRejected) { throw 'Unsigned executable was accepted by native signature verification.' }
Write-Output 'PASS Native signature verification rejects unsigned OpenSweep.'

$signedWindowsExecutable = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
$fixtureSignature = Get-AuthenticodeSignature $signedWindowsExecutable
if ($fixtureSignature.Status -ne 'Valid' -or $fixtureSignature.SignatureType -ne 'Authenticode') {
    throw "The SDK host must have a valid embedded Authenticode signature; got $($fixtureSignature.Status) / $($fixtureSignature.SignatureType)."
}
$trustedKey = Get-TrustedKey $signedWindowsExecutable
if ($trustedKey -notmatch '^[0-9a-f]{64}$') { throw 'A trusted signed Windows executable did not yield a signing identity.' }
Write-Output 'PASS Native signature verification accepts the embedded-signed SDK host and extracts its verified signing key.'

# Read metadata from the exact PE bytes used by signature verification, without MUI sidecars.
$metadataType = $assembly.GetType('OpenSweep.Core.SelfUpdateMetadata', $true)
$readMetadata = $metadataType.GetMethod('Read', [Reflection.BindingFlags]'Static,Public')
$metadataStream = [IO.File]::OpenRead($executable)
try { $metadata = $readMetadata.Invoke($null, [object[]]@($metadataStream)) }
finally { $metadataStream.Dispose() }
if ($metadata.ProductName -ne 'OpenSweep' -or $metadata.FileVersion -ne $assembly.GetName().Version) {
    throw 'Embedded product/version metadata does not match the built OpenSweep assembly.'
}
Write-Output 'PASS Product name and version are read directly from the real OpenSweep PE.'

# Verify native path locks prevent replacement without blocking unrelated sibling file writes.
$fixture = Join-Path $output ('OpenSweep-path-lock-' + [Guid]::NewGuid().ToString('N'))
$staging = Join-Path $fixture 'staging'
$sibling = Join-Path $fixture 'sibling'
[IO.Directory]::CreateDirectory($staging) | Out-Null
[IO.Directory]::CreateDirectory($sibling) | Out-Null
$stagedFile = Join-Path $staging 'fixture.exe'
[IO.File]::WriteAllText($stagedFile, 'original fixture bytes')
$fileLock = $null
try {
    $lockType = $assembly.GetType('OpenSweep.Core.SelfUpdateFileLock', $true)
    $constructor = $lockType.GetConstructor([Type[]]@([string]))
    $fileLock = $constructor.Invoke([object[]]@([string]$stagedFile))
    $writeBlocked = $false
    try { [IO.File]::WriteAllText($stagedFile, 'changed bytes') }
    catch { if ($_.Exception.GetBaseException() -is [IO.IOException]) { $writeBlocked = $true } else { throw } }
    if (-not $writeBlocked) { throw 'A staged file could be changed while verification locks were held.' }
    $renameBlocked = $false
    try { [IO.Directory]::Move($staging, (Join-Path $fixture 'renamed')) }
    catch { if ($_.Exception.GetBaseException() -is [IO.IOException]) { $renameBlocked = $true } else { throw } }
    if (-not $renameBlocked) { throw 'A staged ancestor directory could be renamed while verification locks were held.' }
    $siblingFile = Join-Path $sibling 'still-writable.txt'
    [IO.File]::WriteAllText($siblingFile, 'unrelated writes still work')
    if ([IO.File]::ReadAllText($stagedFile) -ne 'original fixture bytes') { throw 'Staged bytes changed.' }
    Write-Output 'PASS Native verification locks prevent staged-file modification and ancestor rename while allowing sibling writes.'
}
finally {
    if ($null -ne $fileLock) { $fileLock.Dispose() }
    [IO.Directory]::Delete($fixture, $true)
}

$deleteFixture = Join-Path ([IO.Path]::GetTempPath()) ('OpenSweep-delete-' + [Guid]::NewGuid().ToString('N') + '.tmp')
[IO.File]::WriteAllText($deleteFixture, 'temporary deletion-result fixture')
try {
    $native = $assembly.GetType('OpenSweep.Core.NativeMethods', $true)
    $delete = $native.GetMethod('TryDeleteFile', [Reflection.BindingFlags]'Static,Public')
    $arguments = [object[]]@([string]$deleteFixture, 0)
    if (-not $delete.Invoke($null, $arguments) -or $arguments[1] -ne 0) { throw 'Native fixture deletion failed.' }
    $arguments = [object[]]@([string]$deleteFixture, 0)
    if ($delete.Invoke($null, $arguments) -or $arguments[1] -ne 2) { throw 'An already-missing file was counted as deleted.' }
    Write-Output 'PASS Native cleanup result distinguishes deletion from an already-missing file.'
}
finally { [IO.File]::Delete($deleteFixture) }

# Use a plain WPF Application, so production App.OnStartup and command-line actions do not run.
$application = [Windows.Application]::new()
$application.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
$pages = @('HistoryPage', 'SelfUpdatePage', 'SoftwareUpdaterPage', 'WindowsUpdatePage')
$stage = 'initializing application resources'
try {
    foreach ($theme in @('Light', 'Dark')) {
        $stage = "loading $theme theme resources"
        $application.Resources.MergedDictionaries.Clear()
        foreach ($resource in @("Themes/$theme.xaml", 'Themes/Styles.xaml')) {
            $dictionary = [Windows.ResourceDictionary]::new()
            $resourceUri = [Uri]"pack://application:,,,/OpenSweep;component/$resource"
            # ResourceDictionary implements IDictionary. PowerShell's property adapter treats
            # `.Source = ...` as a dictionary entry; call the CLR setter to actually load XAML.
            $dictionary.set_Source($resourceUri)
            $loadedSource = $dictionary.get_Source()
            if ($null -eq $loadedSource -or $loadedSource -ne $resourceUri -or $dictionary.get_Count() -eq 0) {
                throw "Resource dictionary '$resource' did not load from '$resourceUri'."
            }
            $application.Resources.MergedDictionaries.Add($dictionary)
        }
        # Fail at resource loading, with the exact key, before XAML construction wraps the error.
        $requiredResources = @{
            'AppFont' = [Windows.Media.FontFamily]; 'HeadingFont' = [Windows.Media.FontFamily]
            'PageRoot' = [Windows.Style]; 'H1' = [Windows.Style]; 'H2' = [Windows.Style]
            'Sub' = [Windows.Style]; 'Card' = [Windows.Style]; 'WarnBanner' = [Windows.Style]
            'PageBg' = [Windows.Media.Brush]; 'TextMain' = [Windows.Media.Brush]
            'Surface' = [Windows.Media.Brush]; 'Accent' = [Windows.Media.Brush]
        }
        foreach ($key in $requiredResources.Keys) {
            $value = $application.TryFindResource($key)
            if ($null -eq $value -or -not $requiredResources[$key].IsInstanceOfType($value)) {
                throw "Required resource '$key' is missing or has the wrong type in the $theme theme."
            }
        }
        Write-Output "PASS $theme theme loads the required styles, fonts, and brushes."
        foreach ($pageName in $pages) {
            $stage = "constructing $pageName in the $theme theme"
            $page = [Activator]::CreateInstance($assembly.GetType("OpenSweep.Pages.$pageName", $true))
            # Pages normally inherit the MainWindow background. Give the isolated rendered
            # control the same brush so screenshots include the real theme behind its cards.
            $page.SetResourceReference([Windows.Controls.Control]::BackgroundProperty, 'PageBg')
            if ($pageName -eq 'HistoryPage') {
                $stage = "checking the in-memory history fixture in the $theme theme"
                # In-memory fixtures exercise selection and filtering without touching saved history.
                $record = [Activator]::CreateInstance($assembly.GetType('OpenSweep.Core.MaintenanceRecord', $true))
                $record.Operation = 'Cleanup preview fixture'
                $record.Summary = 'Completed items are preserved alongside skipped and failed items.'
                $record.Outcome = [Enum]::Parse($record.Outcome.GetType(), 'Partial')
                $record.FinishedUtc = [DateTime]::UtcNow
                $item = [Activator]::CreateInstance($assembly.GetType('OpenSweep.Core.MaintenanceItem', $true))
                $item.Label = 'Temporary files'
                $item.Status = 'Completed with skipped items'
                $item.Detail = 'Temporary fixture only. No cleanup was performed.'
                $item.Bytes = 10485760L
                $item.Files = 24L
                $item.Skipped = 2L
                $item.Failures = 1L
                $item.BytesKind = [Enum]::Parse($item.BytesKind.GetType(), 'Deleted')
                $item.Outcome = [Enum]::Parse($item.Outcome.GetType(), 'Partial')
                $record.Items.Add($item)
                $flags = [Reflection.BindingFlags]'Instance,NonPublic'
                $page.GetType().GetField('_records', $flags).GetValue($page).Add($record)
                $page.GetType().GetMethod('ApplyFilter', $flags).Invoke($page, $null) | Out-Null
                $search = $page.FindName('SearchBox')
                $search.Text = 'no matching fixture'
                if ($page.FindName('RecordsGrid').Items.Count -ne 0) { throw 'History filter failed to exclude a nonmatching record.' }
                $search.Text = ''
                if ($page.FindName('RecordsGrid').Items.Count -ne 1) { throw 'History filter failed to restore the record.' }
                if ($page.FindName('DetailTotals').Text -notmatch '2 skipped.*1 failed') { throw 'History details omitted skipped/failed totals.' }
            }
            $window = [Windows.Window]::new()
            $window.Width = 1060
            $window.Height = 800
            $window.ShowInTaskbar = $false
            $window.SetResourceReference([Windows.Controls.Control]::BackgroundProperty, 'PageBg')
            $window.Content = $page
            try {
                $stage = "rendering $pageName in the $theme theme"
                $window.Show()
                $window.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::ContextIdle)
                $window.UpdateLayout()
                $width = [int][Math]::Ceiling($page.ActualWidth)
                $height = [int][Math]::Ceiling($page.ActualHeight)
                if ($width -lt 100 -or $height -lt 100) { throw "$pageName has an invalid layout size." }
                $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($width, $height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
                $bitmap.Render($page)
                $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
                $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
                $stream = [IO.File]::Create((Join-Path $output "$pageName-$theme.png"))
                try { $encoder.Save($stream) } finally { $stream.Dispose() }
                Write-Output "PASS $pageName renders in $theme theme ($width x $height)."
            }
            finally { $window.Close() }
        }
    }
}
catch {
    # PowerShell normally displays only TargetInvocationException/XamlParseException's outer
    # message. Emit every cause directly so CI retains missing resource keys and XAML details.
    [Console]::Error.WriteLine("WPF smoke failure while $stage.")
    $cause = $_.Exception
    $depth = 0
    while ($null -ne $cause) {
        [Console]::Error.WriteLine(('Cause {0}: {1}: {2}' -f $depth, $cause.GetType().FullName, $cause.Message))
        $cause = $cause.InnerException
        $depth++
    }
    [Console]::Error.WriteLine($_.Exception.ToString())
    throw
}
finally { $application.Shutdown() }
Write-Output 'Windows smoke checks completed; no maintenance action or installer was executed.'
