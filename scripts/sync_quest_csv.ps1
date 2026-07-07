param(
    [string]$FileName = "rf_trajectory_log.csv",
    [switch]$SyncMesh,
    [string]$MeshFileName = "quest_room_mesh.obj",
    [string]$MeshAssetPath = "Assets\ExportedMeshes\quest_room_mesh.obj",
    [string]$PackageName = "com.DefaultCompany.RadioTwin_Quest_DataCollector",
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$CompanyName = "DefaultCompany",
    [string]$ProductName = "RadioTwin_Quest_DataCollector",
    [string]$AdbPath = ""
)

$ErrorActionPreference = "Stop"

function Find-Adb {
    param([string]$RequestedPath)

    if ($RequestedPath -and (Test-Path -LiteralPath $RequestedPath)) {
        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $unityHubEditors = "C:\Program Files\Unity\Hub\Editor"
    if (Test-Path -LiteralPath $unityHubEditors) {
        $unityAdb = Get-ChildItem -LiteralPath $unityHubEditors -Recurse -Filter adb.exe -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -First 1

        if ($unityAdb) {
            return $unityAdb.FullName
        }
    }

    throw "Could not find adb.exe. Pass -AdbPath with the full path to adb.exe."
}

function Assert-QuestReady {
    param([string]$ResolvedAdbPath)

    $devicesOutput = & $ResolvedAdbPath devices
    $deviceLines = $devicesOutput | Where-Object { $_ -match "^\S+\s+\S+$" -and $_ -notmatch "^List of devices" }

    if (-not $deviceLines -or $deviceLines.Count -eq 0) {
        throw "No Quest/device found. Plug it in, then accept USB debugging in the headset."
    }

    $unauthorized = $deviceLines | Where-Object { $_ -match "\sunauthorized$" }
    if ($unauthorized) {
        throw "Quest is unauthorized. Put on the headset, accept 'Allow USB debugging', then run this again."
    }

    $ready = $deviceLines | Where-Object { $_ -match "\sdevice$" }
    if (-not $ready) {
        throw "ADB sees a device, but it is not ready: $($deviceLines -join '; ')"
    }
}

$adb = Find-Adb -RequestedPath $AdbPath
$questPath = "/sdcard/Android/data/$PackageName/files/$FileName"
$projectCopyPath = Join-Path $ProjectRoot $FileName
$editorPersistentPath = Join-Path $env:USERPROFILE "AppData\LocalLow\$CompanyName\$ProductName"
$editorCopyPath = Join-Path $editorPersistentPath $FileName
$questMeshPath = "/sdcard/Android/data/$PackageName/files/$MeshFileName"
$meshAssetCopyPath = Join-Path $ProjectRoot $MeshAssetPath

Write-Host "Using adb: $adb"
Assert-QuestReady -ResolvedAdbPath $adb

if (-not (Test-Path -LiteralPath $ProjectRoot)) {
    New-Item -ItemType Directory -Path $ProjectRoot | Out-Null
}

if (-not (Test-Path -LiteralPath $editorPersistentPath)) {
    New-Item -ItemType Directory -Path $editorPersistentPath | Out-Null
}

Write-Host "Pulling Quest file:"
Write-Host "  $questPath"
Write-Host "to:"
Write-Host "  $projectCopyPath"
& $adb pull $questPath $projectCopyPath

if (-not (Test-Path -LiteralPath $projectCopyPath)) {
    throw "Pull completed without creating $projectCopyPath"
}

Copy-Item -LiteralPath $projectCopyPath -Destination $editorCopyPath -Force

$projectItem = Get-Item -LiteralPath $projectCopyPath
$editorItem = Get-Item -LiteralPath $editorCopyPath
$lineCount = (Get-Content -LiteralPath $editorCopyPath).Count

Write-Host ""
Write-Host "Synced CSV successfully."
Write-Host "Project copy: $($projectItem.FullName)"
Write-Host "Editor copy:  $($editorItem.FullName)"
Write-Host "Size:         $($editorItem.Length) bytes"
Write-Host "Lines:        $lineCount"

if ($SyncMesh) {
    $meshAssetDirectory = Split-Path -Parent $meshAssetCopyPath

    if (-not (Test-Path -LiteralPath $meshAssetDirectory)) {
        New-Item -ItemType Directory -Path $meshAssetDirectory | Out-Null
    }

    Write-Host ""
    Write-Host "Pulling Quest mesh:"
    Write-Host "  $questMeshPath"
    Write-Host "to:"
    Write-Host "  $meshAssetCopyPath"
    & $adb pull $questMeshPath $meshAssetCopyPath

    if (-not (Test-Path -LiteralPath $meshAssetCopyPath)) {
        throw "Mesh pull completed without creating $meshAssetCopyPath"
    }

    $meshItem = Get-Item -LiteralPath $meshAssetCopyPath
    Write-Host "Mesh copy:    $($meshItem.FullName)"
    Write-Host "Mesh size:    $($meshItem.Length) bytes"
}

Write-Host ""
Write-Host "In Unity, press R or switch visualization modes to reload the CSV."
if ($SyncMesh) {
    Write-Host "If Unity does not refresh the mesh, right-click Assets/ExportedMeshes/quest_room_mesh.obj and choose Reimport."
}
