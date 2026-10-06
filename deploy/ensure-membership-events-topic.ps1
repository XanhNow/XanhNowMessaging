param(
    [string]$Broker = "192.168.2.14"
)

$ErrorActionPreference = "Stop"
$remoteScript = Join-Path $PSScriptRoot "ensure-membership-events-topic.sh"

if (-not (Test-Path -LiteralPath $remoteScript -PathType Leaf)) {
    throw "Kafka topic script does not exist: $remoteScript"
}

Write-Host "=== COPY MEMBERSHIP EVENTS TOPIC SCRIPT TO KAFKA-1 ==="
scp `
    $remoteScript `
    "xanhnow@${Broker}:/tmp/ensure-membership-events-topic.sh"
if ($LASTEXITCODE -ne 0) {
    throw "Copy Kafka topic script failed"
}

Write-Host "`n=== CHECK OR CREATE MEMBERSHIP EVENTS TOPIC ==="
ssh -tt `
    "xanhnow@$Broker" `
    "KAFKA_BOOTSTRAP_SERVER='${Broker}:9092' bash /tmp/ensure-membership-events-topic.sh"
if ($LASTEXITCODE -ne 0) {
    throw "Membership events Kafka topic check/create failed"
}

Write-Host "`nMEMBERSHIP_EVENTS_TOPIC_READY_PASS"
