$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$tabSourcePath = Join-Path $repoRoot 'SftpTabContent.xaml.cs'
$mainWindowSourcePath = Join-Path $repoRoot 'MainWindow.xaml.cs'
$tabSource = Get-Content -LiteralPath $tabSourcePath -Raw
$mainWindowSource = Get-Content -LiteralPath $mainWindowSourcePath -Raw

function Assert-StaticInvariant {
    param(
        [Parameter(Mandatory)]
        [bool] $Condition,
        [Parameter(Mandatory)]
        [string] $Message
    )

    if (-not $Condition) {
        throw "Upload staging cleanup invariant failed: $Message"
    }
}

$registerCount = ([regex]::Matches($tabSource, 'RegisterRemoteUploadStagingPath\([^)]*\);')).Count
$unregisterCount = ([regex]::Matches($tabSource, 'UnregisterRemoteUploadStagingPath\([^)]*\);')).Count

Assert-StaticInvariant ($registerCount -ge 4) `
    'manual upload, folder upload, remote copy, and auto-sync must register their exact staging path.'
Assert-StaticInvariant ($unregisterCount -ge 4) `
    'successful operations and close cleanup must unregister confirmed-clean staging paths.'

$backupRegisterIndex = $tabSource.IndexOf(
    'RegisterRemoteUploadBackupTransaction(backupTransaction);',
    [StringComparison]::Ordinal)
$destinationBackupRenameIndex = $tabSource.IndexOf(
    'client.RenameFile(finalPath, backupPath);',
    $backupRegisterIndex,
    [StringComparison]::Ordinal)
Assert-StaticInvariant (
    $backupRegisterIndex -ge 0 -and
    $destinationBackupRenameIndex -gt $backupRegisterIndex) `
    'the exact backup and destination transaction must be registered before fallback rename.'

$abortIndex = $tabSource.IndexOf('private async Task DrainCloseTasksAndCleanupRemoteUploadsAsync', [StringComparison]::Ordinal)
$waitIndex = $tabSource.IndexOf(
    'await DrainCloseTasksAsync(tasks).ConfigureAwait(false);',
    $abortIndex,
    [StringComparison]::Ordinal)
$closeCleanupIndex = $tabSource.IndexOf(
    'await CleanupRemoteUploadStagingFilesAfterCloseAsync()',
    $waitIndex,
    [StringComparison]::Ordinal)
Assert-StaticInvariant (
    $abortIndex -ge 0 -and
    $waitIndex -gt $abortIndex -and
    $closeCleanupIndex -gt $waitIndex) `
    'normal close must wait for tracked operations and only then clean staging.'

$cleanupStart = $tabSource.IndexOf(
    'private async Task CleanupRemoteUploadStagingFilesAfterCloseAsync()',
    [StringComparison]::Ordinal)
$cleanupEnd = $tabSource.IndexOf(
    'private void CommitRemoteReplacement',
    $cleanupStart,
    [StringComparison]::Ordinal)
Assert-StaticInvariant ($cleanupStart -ge 0 -and $cleanupEnd -gt $cleanupStart) `
    'the bounded close cleanup method must remain present.'

$cleanupSource = $tabSource.Substring($cleanupStart, $cleanupEnd - $cleanupStart)
Assert-StaticInvariant ($cleanupSource.Contains('ConnectAuxiliarySftpAsync(cleanupCts.Token)')) `
    'close cleanup must use a fresh sibling SFTP client.'
Assert-StaticInvariant ($cleanupSource.Contains('ExistsAsync(path, cleanupCts.Token)')) `
    'cleanup must delete only a registered exact staging path with the bounded token.'
Assert-StaticInvariant ($cleanupSource.Contains('_activeRemoteUploadBackupTransactions.Values.ToArray()')) `
    'close cleanup must snapshot only exact registered backup transactions.'
Assert-StaticInvariant ($cleanupSource.Contains('RenameFileAsync(transaction.BackupPath, transaction.DestinationPath, cleanupCts.Token)')) `
    'close cleanup must recover each exact backup transaction with the bounded token.'

$backupRecoveryIndex = $cleanupSource.IndexOf(
    'foreach (var transaction in backups)',
    [StringComparison]::Ordinal)
$stagingCleanupIndex = $cleanupSource.IndexOf(
    'foreach (var path in stagingPaths)',
    [StringComparison]::Ordinal)
Assert-StaticInvariant (
    $backupRecoveryIndex -ge 0 -and
    $stagingCleanupIndex -gt $backupRecoveryIndex) `
    'backup recovery must complete before staging-file deletion starts.'

Assert-StaticInvariant ($cleanupSource.Contains('if (!await cleanupClient.ExistsAsync(transaction.BackupPath, cleanupCts.Token))')) `
    'a confirmed absent backup must retire its transaction.'
Assert-StaticInvariant ($cleanupSource.Contains('if (!await cleanupClient.ExistsAsync(transaction.DestinationPath, cleanupCts.Token))')) `
    'an absent destination must restore its exact registered backup.'
Assert-StaticInvariant ($cleanupSource.Contains('await cleanupClient.DeleteFileAsync(transaction.BackupPath, cleanupCts.Token);')) `
    'a redundant exact backup must be deleted when the destination already exists.'

$forbiddenDiscoveryCalls = @('ListDirectory', 'EnumerateFiles', 'GetFiles', 'SearchFiles')
foreach ($call in $forbiddenDiscoveryCalls) {
    Assert-StaticInvariant (-not $cleanupSource.Contains($call)) `
        "close cleanup must not discover remote files via $call or glob matching."
}

Assert-StaticInvariant ($mainWindowSource.Contains('ReleaseConnectionAfterCleanupAsync(tabContent.CloseCleanupTask, connection)')) `
    'MainWindow must pass the tab cleanup task before releasing its primary client.'
Assert-StaticInvariant ($mainWindowSource.Contains('await closeCleanupTask.ConfigureAwait(false);')) `
    'primary client disposal must await the registered close cleanup task.'

Write-Host 'Upload staging cleanup static scenario passed.'
