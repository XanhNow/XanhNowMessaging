[CmdletBinding()]
param([switch]$ExpectForeignKeyFailure)
$ErrorActionPreference='Stop'
$bin='C:\Program Files\PostgreSQL\18\bin'
$project='C:\BackEndXanhNow\XanhNowMessaging\tests\XanhNow.Messaging.Tests\XanhNow.Messaging.Tests.csproj'
$work=Join-Path $env:TEMP ('s101-messaging-fk-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work)|Out-Null
$data=Join-Path $work 'data'
$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
$started=$false
$saved=[Environment]::GetEnvironmentVariable('S101_MESSAGING_DISPOSABLE_TEST_DB','Process')
try{
 & (Join-Path $bin 'initdb.exe') -D $data -U s101_fk_test --auth-local=trust --auth-host=trust --encoding=UTF8 --no-locale > (Join-Path $work 'init.log') 2>&1
 if($LASTEXITCODE -ne 0){throw 'Temporary init failed.'}
 $started=$true
 $process=Start-Process -FilePath (Join-Path $bin 'pg_ctl.exe') -WindowStyle Hidden -PassThru -ArgumentList "-D $data -l $work/server.log -o ""-h 127.0.0.1 -p $port"" -w start" -RedirectStandardOutput (Join-Path $work 'start.log') -RedirectStandardError (Join-Path $work 'start-error.log')
 if(-not $process.WaitForExit(30000) -or $process.ExitCode -ne 0){throw 'Temporary PostgreSQL start failed.'}
 & (Join-Path $bin 'psql.exe') -X -w -q -h 127.0.0.1 -p $port -U s101_fk_test -d postgres -v ON_ERROR_STOP=1 -c 'CREATE DATABASE s101_messaging_fk_test;'
 if($LASTEXITCODE -ne 0){throw 'Temporary database create failed.'}
 $env:S101_MESSAGING_DISPOSABLE_TEST_DB="Host=127.0.0.1;Port=$port;Database=s101_messaging_fk_test;Username=s101_fk_test;Pooling=false"
 $output=@(dotnet test $project --no-build --no-restore --verbosity minimal --filter FullyQualifiedName=XanhNow.Messaging.Tests.PaymentNotificationPersistenceTests.PaymentCommitCreatesOwnerMailboxAndOutboxAndReplaysDoNotDuplicateThem 2>&1)
 $exitCode=$LASTEXITCODE
 $output|Set-Content -LiteralPath (Join-Path $work 'test.log') -Encoding UTF8
 if($ExpectForeignKeyFailure){
  if($exitCode -eq 0 -or ($output -join "`n") -notmatch '23503' -or ($output -join "`n") -notmatch 'message_outbox_events_message_id_fkey'){
   $output|Select-Object -Last 35|Write-Output
   throw 'Expected original outbox/message FK failure was not reproduced.'
  }
  Write-Output 'CONFIRMED original Messaging processor fails SQLSTATE 23503 on message_outbox_events_message_id_fkey in temporary PostgreSQL.'
 }else{
  if($exitCode -ne 0){$output|Select-Object -Last 35|Write-Output;throw 'Messaging FK regression failed.'}
  Write-Output 'PASS actual Messaging commit, owner mailbox isolation, one outbox/message/recipient and event/business-key replay deduplication.'
 }
} finally {
 [Environment]::SetEnvironmentVariable('S101_MESSAGING_DISPOSABLE_TEST_DB',$saved,'Process')
 if($started){
  $process=Start-Process -FilePath (Join-Path $bin 'pg_ctl.exe') -WindowStyle Hidden -PassThru -ArgumentList "-D $data -m fast -w stop" -RedirectStandardOutput (Join-Path $work 'stop.log') -RedirectStandardError (Join-Path $work 'stop-error.log')
  if(-not $process.WaitForExit(30000) -or $process.ExitCode -ne 0){throw 'Temporary PostgreSQL stop unconfirmed.'}
  Write-Output 'Temporary PostgreSQL stopped. No production database/provider/services used.'
 }
}
