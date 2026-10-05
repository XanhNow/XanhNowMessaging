param(
    [string]$ProjectRoot = "C:\BackEndXanhNow\XanhNowMessaging",
    [string]$PostgresHost = "192.168.2.80",
    [int]$Port = 15432,
    [string]$Database = "authtest",
    [string]$MigratorUser = "s101_xanhnow_messaging_migrator",
    [string]$RuntimeUser = "s101_xanhnow_messaging_app",
    [string]$RootCertificate = "C:\BackEndXanhNow\XanhnowCustomer\secrets\postgresql-root-ca.crt"
)

$ErrorActionPreference = "Stop"

function Convert-SecureStringToPlainText([securestring]$Value) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

if (-not (Test-Path -LiteralPath $RootCertificate -PathType Leaf)) {
    throw "PostgreSQL root certificate does not exist: $RootCertificate"
}

$project = Join-Path $ProjectRoot "src\XanhNow.Messaging.Migrator\XanhNow.Messaging.Migrator.csproj"
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "XanhNowMessaging migrator project does not exist: $project"
}

$securePassword = Read-Host "Password for PostgreSQL user $MigratorUser" -AsSecureString
$plainPassword = Convert-SecureStringToPlainText $securePassword
$secretFile = Join-Path ([IO.Path]::GetTempPath()) "xanhnow-messaging-migrator-$([Guid]::NewGuid().ToString('N')).secret"
$connectionString =
    "Host=$PostgresHost;Port=$Port;Database=$Database;Username=$MigratorUser;" +
    "Password=$plainPassword;SSL Mode=VerifyFull;Root Certificate=$RootCertificate;" +
    "Include Error Detail=false;Search Path=s101_xanhnow_messaging"

try {
    [IO.File]::WriteAllText($secretFile, $connectionString, [Text.UTF8Encoding]::new($false))
    $env:Database__ConnectionString = ""
    $env:Database__ConnectionStringFile = $secretFile
    dotnet run --project $project --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "XanhNowMessaging migration failed" }

    $env:PGPASSWORD = $plainPassword
    $grantSql = @"
GRANT USAGE ON SCHEMA s101_xanhnow_messaging TO $RuntimeUser;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA s101_xanhnow_messaging TO $RuntimeUser;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA s101_xanhnow_messaging TO $RuntimeUser;
ALTER DEFAULT PRIVILEGES FOR ROLE $MigratorUser IN SCHEMA s101_xanhnow_messaging
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO $RuntimeUser;
ALTER DEFAULT PRIVILEGES FOR ROLE $MigratorUser IN SCHEMA s101_xanhnow_messaging
    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO $RuntimeUser;
"@
    $grantSql | psql "host=$PostgresHost port=$Port dbname=$Database user=$MigratorUser sslmode=verify-full sslrootcert=$RootCertificate"
    if ($LASTEXITCODE -ne 0) { throw "XanhNowMessaging runtime grants failed" }
    Write-Host "XANHNOW_MESSAGING_MIGRATION_AND_GRANT_PASS"
}
finally {
    Remove-Item Env:\Database__ConnectionString -ErrorAction SilentlyContinue
    Remove-Item Env:\Database__ConnectionStringFile -ErrorAction SilentlyContinue
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $secretFile) {
        Remove-Item -LiteralPath $secretFile -Force
    }
    $plainPassword = $null
}

