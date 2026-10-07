param(
    [string]$ProjectRoot = "C:\BackEndXanhNow\XanhNowMessaging",
    [string]$PsqlPath = "C:\Program Files\PostgreSQL\18\bin\psql.exe",
    [string]$PostgresHost = "192.168.2.80",
    [int]$AdminPort = 15432,
    [int]$MigrationPort = 15432,
    [int]$RuntimePort = 5432,
    [string]$Database = "authtest",
    [string]$AdminUser = "postgres",
    [string]$VaultPath = "C:\Program Files\HashiCorp\Vault\vault.exe",
    [string]$VaultAddress = "https://192.168.2.81:8200",
    [string]$VaultCaCert = "C:\BackEndXanhNow\XanhnowAuth\XanhNow_Security_App\runtime\trust\vault-ca.crt",
    [string]$PostgresRootCert = "C:\BackEndXanhNow\XanhnowCustomer\secrets\postgresql-root-ca.crt",
    [string]$AppRoleOutputDirectory = "$env:LOCALAPPDATA\Temp\xanhnow-messaging-deploy\approle"
)

$ErrorActionPreference = "Stop"
$runtimeUser = "s101_xanhnow_messaging_app"
$migratorUser = "s101_xanhnow_messaging_migrator"
$schema = "s101_xanhnow_messaging"
$runtimePolicy = "s101-xanhnow-messaging-runtime-prod"
$runtimeRole = "s101-xanhnow-messaging-runtime-prod"

function ConvertTo-PlainText([securestring]$Value) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Read-ConfirmedPassword([string]$UserName) {
    $first = ConvertTo-PlainText (Read-Host "Set password for PostgreSQL user $UserName" -AsSecureString)
    $second = ConvertTo-PlainText (Read-Host "Confirm password for PostgreSQL user $UserName" -AsSecureString)
    try {
        if ([string]::IsNullOrWhiteSpace($first) -or $first.Length -lt 16) {
            throw "Password for $UserName must contain at least 16 characters."
        }
        if ($first -cne $second) {
            throw "Password confirmation does not match for $UserName."
        }
        return $first
    }
    finally {
        $second = $null
    }
}

function ConvertTo-ConnectionStringValue([string]$Value) {
    return '"' + $Value.Replace('"', '""') + '"'
}

function Invoke-Psql([string]$User, [string]$Password, [string]$Sql) {
    $env:PGPASSWORD = $Password
    $env:PGSSLMODE = "verify-full"
    $env:PGSSLROOTCERT = $PostgresRootCert
    $Sql | & $PsqlPath -h $PostgresHost -p $AdminPort -U $User -d $Database -v ON_ERROR_STOP=1
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL command failed for $User with exit code $LASTEXITCODE" }
}

foreach ($requiredPath in @($PsqlPath, $VaultPath, $VaultCaCert, $PostgresRootCert)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required file does not exist: $requiredPath"
    }
}

$migratorProject = Join-Path $ProjectRoot "src\XanhNow.Messaging.Migrator\XanhNow.Messaging.Migrator.csproj"
$runtimePolicyPath = Join-Path $ProjectRoot "deploy\vault\$runtimePolicy.hcl"
foreach ($requiredPath in @($migratorProject, $runtimePolicyPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required project file does not exist: $requiredPath"
    }
}

$adminPassword = ConvertTo-PlainText (Read-Host "Password for PostgreSQL admin user $AdminUser" -AsSecureString)
$runtimePassword = Read-ConfirmedPassword $runtimeUser
$migratorPassword = Read-ConfirmedPassword $migratorUser
if ($runtimePassword -ceq $migratorPassword) {
    throw "Runtime and migrator passwords must be different."
}
$migrationSecretFile = Join-Path ([IO.Path]::GetTempPath()) "xanhnow-messaging-migration-$([Guid]::NewGuid().ToString('N')).secret"

try {
    $runtimePasswordSql = $runtimePassword.Replace("'", "''")
    $migratorPasswordSql = $migratorPassword.Replace("'", "''")
    $provisionSql = @"
DO `$`$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$runtimeUser') THEN
        CREATE ROLE $runtimeUser LOGIN PASSWORD '$runtimePasswordSql';
    ELSE
        ALTER ROLE $runtimeUser WITH LOGIN PASSWORD '$runtimePasswordSql';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$migratorUser') THEN
        CREATE ROLE $migratorUser LOGIN PASSWORD '$migratorPasswordSql';
    ELSE
        ALTER ROLE $migratorUser WITH LOGIN PASSWORD '$migratorPasswordSql';
    END IF;
END
`$`$;

GRANT CONNECT ON DATABASE $Database TO $runtimeUser, $migratorUser;
GRANT CREATE ON DATABASE $Database TO $migratorUser;
CREATE SCHEMA IF NOT EXISTS $schema AUTHORIZATION $migratorUser;
ALTER SCHEMA $schema OWNER TO $migratorUser;
GRANT USAGE, CREATE ON SCHEMA $schema TO $migratorUser;
GRANT USAGE ON SCHEMA $schema TO $runtimeUser;
"@
    Invoke-Psql -User $AdminUser -Password $adminPassword -Sql $provisionSql

    $runtimePasswordValue = ConvertTo-ConnectionStringValue $runtimePassword
    $migratorPasswordValue = ConvertTo-ConnectionStringValue $migratorPassword
    $windowsMigrationConnection = "Host=$PostgresHost;Port=$MigrationPort;Database=$Database;Username=$migratorUser;Password=$migratorPasswordValue;SSL Mode=VerifyFull;Root Certificate=$PostgresRootCert;Include Error Detail=false;Search Path=$schema;Pooling=false;Timeout=15;Command Timeout=60"
    [IO.File]::WriteAllText($migrationSecretFile, $windowsMigrationConnection, [Text.UTF8Encoding]::new($false))

    $env:Database__ConnectionString = ""
    $env:Database__ConnectionStringFile = $migrationSecretFile
    dotnet run --project $migratorProject --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "XanhNowMessaging migration failed" }

    $grantSql = @"
GRANT USAGE ON SCHEMA $schema TO $runtimeUser;
REVOKE CREATE ON SCHEMA $schema FROM $runtimeUser;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA $schema TO $runtimeUser;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA $schema TO $runtimeUser;
ALTER DEFAULT PRIVILEGES FOR ROLE $migratorUser IN SCHEMA $schema
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO $runtimeUser;
ALTER DEFAULT PRIVILEGES FOR ROLE $migratorUser IN SCHEMA $schema
    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO $runtimeUser;
"@
    Invoke-Psql -User $migratorUser -Password $migratorPassword -Sql $grantSql

    $env:VAULT_ADDR = $VaultAddress
    $env:VAULT_CACERT = $VaultCaCert
    $linuxPostgresRootCert = "/etc/xanhnow/s101/postgresql/trust/postgresql-root-ca.crt"
    $runtimeConnection = "Host=$PostgresHost;Port=$RuntimePort;Database=$Database;Username=$runtimeUser;Password=$runtimePasswordValue;SSL Mode=Prefer;Pooling=true;No Reset On Close=true;Timeout=15;Command Timeout=30"
    $migrationConnection = "Host=$PostgresHost;Port=$MigrationPort;Database=$Database;Username=$migratorUser;Password=$migratorPasswordValue;SSL Mode=VerifyFull;Root Certificate=$linuxPostgresRootCert;Search Path=$schema;Pooling=false;Timeout=15;Command Timeout=60"

    & $VaultPath kv put kv/xanhnow/s101/messaging/postgres/runtime "connection_string=$runtimeConnection" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Vault runtime database secret write failed" }
    & $VaultPath kv put kv/xanhnow/s101/messaging/postgres/migration "connection_string=$migrationConnection" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Vault migration database secret write failed" }
    & $VaultPath policy write $runtimePolicy $runtimePolicyPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Vault runtime policy write failed" }
    & $VaultPath write "auth/approle/role/$runtimeRole" `
        "token_policies=$runtimePolicy" `
        "token_ttl=1h" `
        "token_max_ttl=24h" `
        "secret_id_ttl=720h" `
        "secret_id_num_uses=0" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Vault runtime AppRole write failed" }

    $roleId = (& $VaultPath read -field=role_id "auth/approle/role/$runtimeRole/role-id").Trim()
    if ($LASTEXITCODE -ne 0) { throw "Vault runtime role_id read failed" }
    $secretId = (& $VaultPath write -field=secret_id -f "auth/approle/role/$runtimeRole/secret-id").Trim()
    if ($LASTEXITCODE -ne 0) { throw "Vault runtime secret_id creation failed" }

    New-Item -ItemType Directory -Force -Path $AppRoleOutputDirectory | Out-Null
    [IO.File]::WriteAllText((Join-Path $AppRoleOutputDirectory "role_id"), $roleId, [Text.Encoding]::ASCII)
    [IO.File]::WriteAllText((Join-Path $AppRoleOutputDirectory "secret_id"), $secretId, [Text.Encoding]::ASCII)

    Write-Host "messaging_postgres_roles_ready=true"
    Write-Host "messaging_schema_migration_pass=true"
    Write-Host "messaging_vault_database_secrets_ready=true"
    Write-Host "messaging_runtime_approle_ready=true"
    Write-Host "secret_printed=false"
    Write-Host "XANHNOW_MESSAGING_INFRASTRUCTURE_BOOTSTRAP_PASS"
}
finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:\PGSSLMODE -ErrorAction SilentlyContinue
    Remove-Item Env:\PGSSLROOTCERT -ErrorAction SilentlyContinue
    Remove-Item Env:\Database__ConnectionString -ErrorAction SilentlyContinue
    Remove-Item Env:\Database__ConnectionStringFile -ErrorAction SilentlyContinue
    Remove-Item Env:\VAULT_ADDR -ErrorAction SilentlyContinue
    Remove-Item Env:\VAULT_CACERT -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $migrationSecretFile) {
        Remove-Item -LiteralPath $migrationSecretFile -Force
    }
    $adminPassword = $null
    $runtimePassword = $null
    $migratorPassword = $null
}
