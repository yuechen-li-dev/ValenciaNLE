[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$PreviousSetup,
    [Parameter(Mandatory)][string]$TestRoot,
    [Parameter(Mandatory)][string]$Report,
    [string]$CompatibilityFfmpeg
)
$ErrorActionPreference = 'Stop'
$packagePath = (Resolve-Path -LiteralPath $Package).Path
$previous = (Resolve-Path -LiteralPath $PreviousSetup).Path
$testPath = [IO.Path]::GetFullPath($TestRoot)
$temp = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
if (-not $testPath.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Use a test folder under Windows TEMP.' }
$install = Join-Path $testPath 'Install'
$registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{E84B7F78-E0F1-457B-9870-74509182D17D}_is1'
$existing = Get-ItemProperty -LiteralPath $registry -ErrorAction SilentlyContinue
if ($existing -and $existing.InstallLocation.TrimEnd('\') -ne $install) { throw 'A separate Valencia installation is registered.' }

$reportPath = [IO.Path]::GetFullPath($Report)
if (Test-Path -LiteralPath $reportPath) { throw 'Choose a new qualification report directory.' }
New-Item -ItemType Directory -Path $testPath -Force | Out-Null
New-Item -ItemType Directory -Path $reportPath | Out-Null
$setup = (Get-ChildItem -LiteralPath $packagePath -Filter '*-setup.exe').FullName
$zip = (Get-ChildItem -LiteralPath $packagePath -Filter '*-portable.zip').FullName
$portable = Join-Path $testPath 'Portable'
Expand-Archive -LiteralPath $zip -DestinationPath $portable
$proof = Join-Path $PSScriptRoot 'M3bReleaseProof.py'

function RunSetup([string]$file, [string]$log, [string[]]$additional = @()) {
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART', ('/LOG="' + $log + '"')) + $additional
    $process = Start-Process -FilePath $file -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Setup action returned $($process.ExitCode). See $log" }
}

function RunProof([string]$folder, [string]$kind, [string]$name, [string[]]$additional = @()) {
    $arguments = @($proof,'--bundle',$folder,'--kind',$kind,'--report',(Join-Path $reportPath $name)) + $additional
    & python @arguments
    if ($LASTEXITCODE -ne 0) { throw "$kind qualification failed." }
}

$data = (New-Item -ItemType Directory -Path (Join-Path $testPath 'user-data') -Force).FullName
$sentinel = Join-Path $data 'qualification-settings.json'
'{"proof":"Valencia upgrade retains user data"}' | Set-Content -LiteralPath $sentinel
$before = (Get-FileHash -LiteralPath $sentinel).Hash
$location = '/DIR="' + $install + '"'
RunSetup $previous (Join-Path $reportPath 'install-previous.log') @($location)
$oldVersion = (Get-ItemProperty -LiteralPath $registry).DisplayVersion
RunSetup $setup (Join-Path $reportPath 'upgrade.log') @($location)
$registration = Get-ItemProperty -LiteralPath $registry
if ($oldVersion -eq $registration.DisplayVersion) { throw 'The upgrade did not change the installed version.' }
if ($before -ne (Get-FileHash -LiteralPath $sentinel).Hash) { throw 'Upgrade changed the test data.' }
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Valencia/Valencia.lnk'
$shell = New-Object -ComObject WScript.Shell
if (-not (Test-Path -LiteralPath $shortcut) -or $shell.CreateShortcut($shortcut).TargetPath -ne (Join-Path $install 'Valencia.App.exe')) {
    throw 'The installed Start menu shortcut does not target the qualified application.'
}
$footprint = Get-ChildItem -LiteralPath $install -File -Recurse | Measure-Object Length -Sum
RunProof $portable 'portable' 'portable.json'
RunProof $install 'installed' 'installed.json'
if ($CompatibilityFfmpeg) {
    RunProof $install 'installed' 'compatibility.json' @('--compat', $CompatibilityFfmpeg, '--no-crash')
}
RunSetup (Join-Path $install 'unins000.exe') (Join-Path $reportPath 'uninstall.log')
if (Test-Path -LiteralPath (Join-Path $install 'Valencia.App.exe')) { throw 'Uninstall left the executable in place.' }
if (Test-Path -LiteralPath $registry) { throw 'Uninstall left the product registered.' }
if ($before -ne (Get-FileHash -LiteralPath $sentinel).Hash) { throw 'Uninstall changed the test data.' }
[ordered]@{
    previousVersion=$oldVersion; installedVersion=$registration.DisplayVersion; installLocation=$install
    installedBytes=[long]$footprint.Sum; installedFiles=$footprint.Count; shortcutTarget=(Join-Path $install 'Valencia.App.exe')
    signature=(Get-AuthenticodeSignature -LiteralPath $setup).Status.ToString(); testDataSha256=$before
    setupSha256=(Get-FileHash -LiteralPath $setup).Hash; portableSha256=(Get-FileHash -LiteralPath $zip).Hash
    upgradePreservedData=$true; uninstallPreservedData=$true
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reportPath 'lifecycle.json')
