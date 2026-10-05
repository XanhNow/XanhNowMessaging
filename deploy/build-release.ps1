param(
    [string]$ProjectRoot = "C:\BackEndXanhNow\XanhNowMessaging"
)

$ErrorActionPreference = "Stop"

$apiProject = Join-Path $ProjectRoot "src\XanhNow.Messaging.Api\XanhNow.Messaging.Api.csproj"
$migratorProject = Join-Path $ProjectRoot "src\XanhNow.Messaging.Migrator\XanhNow.Messaging.Migrator.csproj"
$artifactRoot = Join-Path $ProjectRoot "artifacts"
$apiPublish = Join-Path $artifactRoot "api-linux"
$migratorPublish = Join-Path $artifactRoot "migrator-linux"
$stage = Join-Path $artifactRoot "release-stage"
$release = Join-Path $ProjectRoot "release"
$archive = Join-Path $release "xanhnow-messaging-linux.zip"
$shaFile = "$archive.sha256"

Remove-Item -LiteralPath $apiPublish, $migratorPublish, $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $apiPublish, $migratorPublish, $stage, $release -Force | Out-Null

dotnet publish $apiProject --configuration Release --runtime linux-x64 --self-contained false --no-restore --output $apiPublish
if ($LASTEXITCODE -ne 0) { throw "XanhNowMessaging API publish failed" }

dotnet publish $migratorProject --configuration Release --runtime linux-x64 --self-contained false --no-restore --output $migratorPublish
if ($LASTEXITCODE -ne 0) { throw "XanhNowMessaging Migrator publish failed" }

$stageApi = Join-Path $stage "api"
$stageMigrator = Join-Path $stage "migrator"
$stageDeploy = Join-Path $stage "deploy"
$stageVault = Join-Path $stageDeploy "vault-agent"
New-Item -ItemType Directory -Path $stageApi, $stageMigrator, $stageDeploy, $stageVault -Force | Out-Null
Copy-Item -Path (Join-Path $apiPublish "*") -Destination $stageApi -Recurse -Force
Copy-Item -Path (Join-Path $migratorPublish "*") -Destination $stageMigrator -Recurse -Force
Copy-Item -LiteralPath (Join-Path $ProjectRoot "deploy\systemd\xanhnow-messaging-api.service") -Destination $stageDeploy -Force
Copy-Item -LiteralPath (Join-Path $ProjectRoot "deploy\systemd\xanhnow-messaging-vault-agent.service") -Destination $stageDeploy -Force
Copy-Item -LiteralPath (Join-Path $ProjectRoot "deploy\vault-agent\vault-agent.hcl") -Destination $stageVault -Force
Copy-Item -LiteralPath (Join-Path $ProjectRoot "deploy\vault-agent\messaging-migrator.hcl") -Destination $stageVault -Force
Copy-Item -LiteralPath (Join-Path $ProjectRoot "deploy\vault-agent\templates") -Destination $stageVault -Recurse -Force
Copy-Item -LiteralPath (Join-Path $ProjectRoot "deploy\provision-vault-agent.sh") -Destination $stageDeploy -Force
Copy-Item -LiteralPath (Join-Path $ProjectRoot "deploy\install-node.sh") -Destination $stageDeploy -Force

Remove-Item -LiteralPath $archive, $shaFile -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $archive -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(
    $shaFile,
    "$hash  $(Split-Path $archive -Leaf)`n",
    [Text.UTF8Encoding]::new($false))

Write-Host "XANHNOW_MESSAGING_RELEASE_BUILD_PASS"
Write-Host "archive=$archive"
Write-Host "sha256=$shaFile"
