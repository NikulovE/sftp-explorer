[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string] $Platform = 'x64',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $VcpkgRoot = $env:VcpkgRoot
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$terminalRoot = Join-Path $repositoryRoot 'Microsoft.Terminal.WinUI3\external\Terminal'
$terminalProject = Join-Path $terminalRoot 'src\cascadia\TerminalControl\dll\TerminalControl.vcxproj'
$proxyProject = Join-Path $terminalRoot 'src\host\proxy\Host.Proxy.vcxproj'
$nativeOutput = Join-Path $terminalRoot "bin\$Platform\$Configuration\Microsoft.Terminal.Control\Microsoft.Terminal.Control.dll"

foreach ($requiredPath in @($terminalRoot, $terminalProject, $proxyProject)) {
    if (!(Test-Path -LiteralPath $requiredPath)) {
        throw "Required Terminal source path is missing: $requiredPath. Initialize the Microsoft.Terminal.WinUI3 submodule recursively."
    }
}

if ([string]::IsNullOrWhiteSpace($VcpkgRoot) -or
    !(Test-Path -LiteralPath (Join-Path $VcpkgRoot 'vcpkg.exe') -PathType Leaf)) {
    throw 'VcpkgRoot must name a bootstrapped, full vcpkg checkout containing vcpkg.exe.'
}

$baselineCommit = '927f62e4b8838bd7e441e9c45103a16ffd75007e'
$baselineReference = $baselineCommit + '^{commit}'
& git -C $VcpkgRoot rev-parse --verify $baselineReference 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "The vcpkg checkout does not contain the Terminal baseline $baselineCommit. Fetch its full history before building."
}

$nuget = Join-Path $terminalRoot 'dep\nuget\nuget.exe'
$packagesDirectory = Join-Path $terminalRoot 'packages'
if (!(Test-Path -LiteralPath $nuget -PathType Leaf)) {
    throw 'The embedded Terminal NuGet executable is missing.'
}

$requiredPackages = @(
    [PSCustomObject]@{ Id = 'Microsoft.Windows.CppWinRT'; Version = '2.0.250303.1' },
    [PSCustomObject]@{ Id = 'Microsoft.UI.Xaml'; Version = '2.8.4' },
    [PSCustomObject]@{ Id = 'Microsoft.Web.WebView2'; Version = '1.0.1661.34' },
    [PSCustomObject]@{ Id = 'Microsoft.Windows.ImplementationLibrary'; Version = '1.0.250325.1' }
)
foreach ($package in $requiredPackages) {
    $packageDirectory = Join-Path $packagesDirectory "$($package.Id).$($package.Version)"
    if (Test-Path -LiteralPath $packageDirectory -PathType Container) {
        continue
    }

    & $nuget install $package.Id -Version $package.Version -OutputDirectory $packagesDirectory -NonInteractive
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $packageDirectory -PathType Container)) {
        throw "Installing the native Terminal prerequisite $($package.Id) $($package.Version) failed."
    }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    throw "Visual Studio locator is missing: $vswhere"
}

$msbuild = @(& $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
    -find 'MSBuild\Current\Bin\amd64\MSBuild.exe') | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($msbuild) -or !(Test-Path -LiteralPath $msbuild -PathType Leaf)) {
    throw 'The 64-bit Visual Studio MSBuild executable was not found.'
}

$env:VcpkgRoot = $VcpkgRoot
$solutionDirectory = $terminalRoot.TrimEnd('\') + '\'
foreach ($project in @($proxyProject, $terminalProject)) {
    $buildArguments = @(
        $project,
        '/t:Build',
        '/m',
        '/v:minimal',
        "/p:Configuration=$Configuration",
        "/p:Platform=$Platform",
        "/p:SolutionDir=$solutionDirectory"
    )
    & $msbuild @buildArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Native Terminal build failed for $project with exit code $LASTEXITCODE."
    }
}

if (!(Test-Path -LiteralPath $nativeOutput -PathType Leaf)) {
    throw "Native Terminal build completed without its expected DLL: $nativeOutput"
}

Write-Host "Built native TerminalControl: $nativeOutput"
