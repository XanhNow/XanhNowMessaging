#!/usr/bin/env bash
set -Eeuo pipefail

release_archive="${1:?release archive is required}"
release_sha_file="${2:?release SHA-256 file is required}"
release_id="${3:?release id is required}"
expected_node="${4:-}"

app_user="xanhnow"
app_root="/srv/xanhnow/apps/messaging"
release_dir="$app_root/releases/$release_id"
current_link="$app_root/current"
service_name="xanhnow-messaging-api.service"

echo "XANHNOW_MESSAGING_DEPLOY_START"
echo "secret_printed=false"

fail() {
    echo "status=FAIL"
    echo "reason=$1"
    exit 1
}

if [[ -n "$expected_node" ]]; then
    [[ "$(hostname -s)" == "$expected_node" ]] || fail "wrong_target_host"
fi

id "$app_user" >/dev/null 2>&1 || fail "app_user_missing"
command -v dotnet >/dev/null 2>&1 || fail "dotnet_runtime_missing"
command -v curl >/dev/null 2>&1 || fail "curl_missing"
command -v sha256sum >/dev/null 2>&1 || fail "sha256sum_missing"
command -v python3 >/dev/null 2>&1 || fail "python3_missing"

required_files=(
    /srv/xanhnow/s101/secrets/messaging/postgres-connection-string
    /srv/xanhnow/s101/secrets/messaging/security-jwt-signing-key
    /srv/xanhnow/s101/secrets/messaging/redis-configuration
    /srv/xanhnow/s101/secrets/messaging/redis-password
    /srv/xanhnow/s101/secrets/messaging/kafka-password
    /srv/xanhnow/s101/secrets/messaging/device-token-encryption-key
    /etc/xanhnow/s101/security/trust/kafka-ca.crt
)

for required_file in "${required_files[@]}"; do
    sudo -u "$app_user" test -r "$required_file" || fail "runtime_file_missing_or_unreadable:$required_file"
done

expected_hash="$(awk '{print tolower($1)}' "$release_sha_file")"
actual_hash="$(sha256sum "$release_archive" | awk '{print tolower($1)}')"
[[ -n "$expected_hash" && "$expected_hash" == "$actual_hash" ]] || fail "release_sha256_mismatch"

install -d -o root -g root -m 0755 "$app_root/releases"
rm -rf "$release_dir"
install -d -o root -g root -m 0755 "$release_dir"

python3 - "$release_archive" "$release_dir" <<'PY'
import sys
import zipfile
from pathlib import Path

target = Path(sys.argv[2]).resolve()
with zipfile.ZipFile(sys.argv[1]) as archive:
    for info in archive.infolist():
        normalized = info.filename.replace("\\", "/").strip("/")
        if not normalized:
            continue
        destination = (target / normalized).resolve()
        if destination != target and target not in destination.parents:
            raise RuntimeError(f"unsafe archive entry: {info.filename}")
        if info.is_dir() or info.filename.endswith(("/", "\\")):
            destination.mkdir(parents=True, exist_ok=True)
            continue
        destination.parent.mkdir(parents=True, exist_ok=True)
        with archive.open(info) as source, open(destination, "wb") as output:
            output.write(source.read())
PY

test -s "$release_dir/api/XanhNow.Messaging.Api.dll" || fail "api_dll_missing"
test -s "$release_dir/migrator/XanhNow.Messaging.Migrator.dll" || fail "migrator_dll_missing"
chown -R root:root "$release_dir"
find "$release_dir" -type d -exec chmod 0755 {} +
find "$release_dir" -type f -exec chmod 0644 {} +

install -o root -g root -m 0644 \
    "$release_dir/deploy/xanhnow-messaging-api.service" \
    "/etc/systemd/system/$service_name"

sudo -u "$app_user" env \
    Database__ConnectionStringFile=/srv/xanhnow/s101/secrets/messaging/postgres-connection-string \
    dotnet "$release_dir/migrator/XanhNow.Messaging.Migrator.dll" --check \
    || fail "schema_check_failed"

if [[ -L "$current_link" ]]; then
    rm -f "$current_link"
elif [[ -e "$current_link" ]]; then
    mv "$current_link" "$app_root/current.backup.$release_id"
fi
ln -s "$release_dir" "$current_link"

systemctl daemon-reload || fail "systemd_daemon_reload_failed"
systemctl enable "$service_name" >/dev/null || fail "systemd_enable_failed"
systemctl restart "$service_name" || {
    journalctl -u "$service_name" -n 100 --no-pager || true
    fail "systemd_restart_failed"
}

for _ in {1..30}; do
    if curl -fsS http://127.0.0.1:5052/health/ready >/dev/null; then
        break
    fi
    sleep 1
done

systemctl is-active --quiet "$service_name" || fail "service_not_active"
curl -fsS http://127.0.0.1:5052/health/live >/dev/null || fail "health_live_failed"
curl -fsS http://127.0.0.1:5052/health/ready >/dev/null || fail "health_ready_failed"
ss -lnt | grep -q '127.0.0.1:5052' || fail "loopback_listener_missing"

echo "status=PASS"
echo "service_active=true"
echo "health_live_pass=true"
echo "health_ready_pass=true"
echo "bind_loopback_only=true"
echo "secret_printed=false"
echo "XANHNOW_MESSAGING_DEPLOY_PASS"

