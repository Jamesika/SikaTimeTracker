param(
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $projectRoot "SikaTimeTracker.sln"
$projectPath = Join-Path $projectRoot "src\SikaTimeTracker.App\SikaTimeTracker.App.csproj"
$publishDirectory = [IO.Path]::GetFullPath((Join-Path $projectRoot "artifacts\publish\$Runtime"))
$expectedPublishRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot "artifacts\publish")) + [IO.Path]::DirectorySeparatorChar
if (-not $publishDirectory.StartsWith($expectedPublishRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Resolved publish directory is outside the expected project artifact root."
}

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

foreach ($proxyName in @("HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY")) {
    $proxyValue = [Environment]::GetEnvironmentVariable($proxyName)
    if ([string]::IsNullOrWhiteSpace($proxyValue)) {
        continue
    }

    $proxyUri = $null
    try {
        $proxyUri = [System.Uri]::new($proxyValue, [System.UriKind]::Absolute)
    }
    catch {
        $proxyUri = $null
    }

    if ($null -eq $proxyUri -or -not $proxyUri.IsLoopback) {
        continue
    }

    $proxyClient = [System.Net.Sockets.TcpClient]::new()
    try {
        $connectTask = $proxyClient.ConnectAsync($proxyUri.Host, $proxyUri.Port)
        $proxyIsAvailable = $connectTask.Wait(750) -and $proxyClient.Connected
    }
    catch {
        $proxyIsAvailable = $false
    }
    finally {
        $proxyClient.Dispose()
    }

    if (-not $proxyIsAvailable) {
        [Environment]::SetEnvironmentVariable($proxyName, $null)
    }
}

dotnet test $solutionPath --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Tests failed. Publish was cancelled."
}

dotnet restore $projectPath --runtime $Runtime -p:PublishReadyToRun=true
if ($LASTEXITCODE -ne 0) {
    throw "Restore failed. Publish was cancelled."
}

dotnet publish $projectPath `
    --configuration Release `
    --runtime $Runtime `
    --no-restore `
    -p:PublishProfile=$Runtime
if ($LASTEXITCODE -ne 0) {
    $removedLoopbackProxy = $false
    foreach ($proxyName in @("HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY")) {
        $proxyValue = [Environment]::GetEnvironmentVariable($proxyName)
        $proxyUri = $null
        try {
            $proxyUri = [System.Uri]::new($proxyValue, [System.UriKind]::Absolute)
        }
        catch {
            $proxyUri = $null
        }

        if ($null -ne $proxyUri -and $proxyUri.IsLoopback) {
            [Environment]::SetEnvironmentVariable($proxyName, $null)
            $removedLoopbackProxy = $true
        }
    }

    if ($removedLoopbackProxy) {
        Write-Warning "Publish failed through a loopback proxy; retrying with direct access for this process."
        dotnet publish $projectPath `
            --configuration Release `
            --runtime $Runtime `
            --no-restore `
            -p:PublishProfile=$Runtime
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed."
    }
}

Get-ChildItem -LiteralPath $publishDirectory -Filter "*.pdb" -File | Remove-Item -Force
$publishedFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File)
if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne "SikaTimeTracker.exe") {
    throw "Portable output must contain exactly one SikaTimeTracker.exe file."
}

Write-Host "Portable executable created in: $publishDirectory"
