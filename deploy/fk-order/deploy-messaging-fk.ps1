[CmdletBinding()]
param([switch]$Apply)
$ErrorActionPreference='Stop'
$latest=Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'latest-messaging-fk.json')|ConvertFrom-Json
if($latest.candidate -notmatch '^messaging-fk-[0-9TZ]+$'){throw 'Invalid release.'}
$root=Join-Path $PSScriptRoot $latest.candidate
$manifestPath=Join-Path $root 'release.json'
function Hash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
if((Hash $manifestPath) -ne $latest.manifest_sha256){throw 'Manifest mismatch.'}
$m=Get-Content -Raw -LiteralPath $manifestPath|ConvertFrom-Json
if($m.release -ne $latest.candidate -or $m.source_commit -ne '8e0e298d1346d6da7172c18d8f353821e9150954' -or $m.unit -ne 'xanhnow-messaging-api.service'){throw 'Reviewed Messaging FK release required.'}
$archive=Join-Path $root 'messaging-api.zip'
$helper=Join-Path $PSScriptRoot 'messaging-fk-node.py'
if((Hash $archive) -ne $m.archive_sha256 -or (Hash $helper) -ne $m.node_sha256){throw 'Archive or helper hash mismatch.'}
$encoded=[Convert]::ToBase64String([IO.File]::ReadAllBytes($manifestPath))
$memory=[IO.MemoryStream]::new();$gzip=[IO.Compression.GZipStream]::new($memory,[IO.Compression.CompressionMode]::Compress,$true)
try{$bytes=[IO.File]::ReadAllBytes($helper);$gzip.Write($bytes,0,$bytes.Length)}finally{$gzip.Dispose()}
try{$packed=[Convert]::ToBase64String($memory.ToArray())}finally{$memory.Dispose()}
if($packed.Length+$encoded.Length+600 -gt 27000){throw 'SSH payload too long.'}
$bootstrap='import base64,gzip,sys;p=sys.argv.pop(1);exec(gzip.decompress(base64.b64decode(p)))'
Write-Host 'MESSAGING API ONLY .25/.38/.65: all-node read-only preflight, then sequential activation with -Apply.'
Write-Host 'Keep existing content root/appsettings/environment and credential files; only add a Messaging ExecStart override pointing to the sealed API release.'
Write-Host 'Only Messaging restart. No migration/manual SQL, callback, offset reset or manual event replay. Security/login, Membership/worker/children and Vault/Agents preserved.'
Write-Host 'Existing queued Kafka events may process normally after restart. Liveness/readiness GET only; no phone acceptance claim.'
$nodes=@('192.168.2.25','192.168.2.38','192.168.2.65')
$receiptPath=Join-Path $PSScriptRoot ($m.release+'-deployment-receipt.json')
if($Apply -and (Test-Path -LiteralPath $receiptPath)){throw 'Prior apply receipt exists. Preserve it; no automatic retry or rollback.'}
foreach($node in $nodes){
 Write-Host "=== $node READ ONLY MESSAGING PREFLIGHT: SSH then sudo ==="
 & ssh -tt -o StrictHostKeyChecking=yes -o UpdateHostKeys=no -o ConnectTimeout=15 -o ServerAliveInterval=15 -o ServerAliveCountMax=3 "xanhnow@$node" "sudo -p `$'Sudo password (hidden):\n' python3 -B -c '$bootstrap' $packed Check $node $encoded"
 if($LASTEXITCODE -ne 0){throw "STOP preflight $node. No upload or activation attempted."}
}
if(-not $Apply){Write-Host 'READ ONLY PREFLIGHT COMPLETE. No upload or activation.';return}
$receipt=[ordered]@{release=$m.release;completed=$false;nodes=@();migrationExecuted=$false;callbackSent=$false}
foreach($node in $nodes){$receipt.nodes+=[pscustomobject]@{node=$node;uploadAttempted=$false;activationAttempted=$false;activationConfirmed=$false}}
$reserve=[IO.File]::Open($receiptPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try{$bytes=[Text.Encoding]::UTF8.GetBytes(($receipt|ConvertTo-Json -Depth 6));$reserve.Write($bytes,0,$bytes.Length)}finally{$reserve.Dispose()}
foreach($row in $receipt.nodes){
 $node=$row.node;$remote='/tmp/'+$m.release
 $row.uploadAttempted=$true;$receipt|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $receiptPath -Encoding UTF8
 Write-Host "=== $node UPLOAD MESSAGING API ONLY: SSH password ==="
 & ssh -o StrictHostKeyChecking=yes -o UpdateHostKeys=no -o ConnectTimeout=15 "xanhnow@$node" "umask 077 && mkdir '$remote'"
 if($LASTEXITCODE -ne 0){throw 'Upload directory unavailable or already exists. Preserve receipt; earlier nodes may have changed.'}
 & scp -o StrictHostKeyChecking=yes -o UpdateHostKeys=no -o ConnectTimeout=15 $manifestPath $archive "xanhnow@${node}:$remote/"
 if($LASTEXITCODE -ne 0){throw 'Upload failed. Preserve receipt; no automatic retry.'}
 $row.activationAttempted=$true;$receipt|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $receiptPath -Encoding UTF8
 Write-Host "=== $node ACTIVATE MESSAGING ONLY: SSH then sudo ==="
 & ssh -tt -o StrictHostKeyChecking=yes -o UpdateHostKeys=no -o ConnectTimeout=15 -o ServerAliveInterval=15 -o ServerAliveCountMax=3 "xanhnow@$node" "sudo -p `$'Sudo password (hidden):\n' python3 -B -c '$bootstrap' $packed Apply $node '$remote' '$($latest.manifest_sha256)'"
 if($LASTEXITCODE -ne 0){throw "STOP partial activation $node. Keep receipt; no automatic retry/rollback or other service restart."}
 $row.activationConfirmed=$true;$receipt|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $receiptPath -Encoding UTF8
}
$receipt.completed=$true;$receipt|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $receiptPath -Encoding UTF8
Write-Host 'MESSAGING FK FIX ACTIVE ON ALL 3 NODES. No migration, provider/callback request or credential/Agent change. Existing queue can resume; verify notifications on the phone.'
