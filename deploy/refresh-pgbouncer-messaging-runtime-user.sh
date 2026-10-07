#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

admin_host="${DB_ADMIN_HOST:-192.168.2.80}"
admin_port="${DB_ADMIN_PORT:-15432}"
database="${DB_NAME:-authtest}"
runtime_user="s101_xanhnow_messaging_app"
userlist="${PGBOUNCER_USERLIST:-/etc/pgbouncer/userlist.txt}"

fail() {
    echo "status=FAIL"
    echo "reason=$1"
    exit 1
}

[[ "${EUID}" -eq 0 ]] || fail "root_required"
command -v psql >/dev/null 2>&1 || fail "psql_missing"
command -v sudo >/dev/null 2>&1 || fail "sudo_missing"
[[ -f "$userlist" ]] || fail "pgbouncer_userlist_missing"
systemctl is-active --quiet pgbouncer.service || fail "pgbouncer_inactive"

owner="$(stat -c %U "$userlist")"
group="$(stat -c %G "$userlist")"
mode="$(stat -c %a "$userlist")"
backup="${userlist}.bak.$(date -u +%Y%m%dT%H%M%SZ)"
tmp="$(mktemp)"
fragment="$(mktemp)"
trap 'rm -f "$tmp" "$fragment"' EXIT

install -o "$owner" -g "$group" -m "$mode" "$userlist" "$backup"

sudo -u postgres psql \
    -h "$admin_host" \
    -p "$admin_port" \
    -U postgres \
    -d "$database" \
    -v ON_ERROR_STOP=1 \
    -Atc "SELECT chr(34) || rolname || chr(34) || ' ' || chr(34) || rolpassword || chr(34) FROM pg_authid WHERE rolname = '$runtime_user' AND rolcanlogin" \
    > "$fragment" || fail "postgres_role_lookup_failed"

[[ -s "$fragment" ]] || fail "runtime_role_missing_or_not_login"
grep -qE '^"s101_xanhnow_messaging_app" "SCRAM-SHA-256\$' "$fragment" || fail "runtime_role_scram_verifier_missing"

grep -vE '^"s101_xanhnow_messaging_app" ' "$userlist" > "$tmp" || true
cat "$fragment" >> "$tmp"
install -o "$owner" -g "$group" -m "$mode" "$tmp" "$userlist"

systemctl reload pgbouncer.service || fail "pgbouncer_reload_failed"
grep -qE '^"s101_xanhnow_messaging_app" "SCRAM-SHA-256\$' "$userlist" || fail "userlist_verification_failed"

echo "status=PASS"
echo "runtime_user=$runtime_user"
echo "verifier_type=SCRAM-SHA-256"
echo "backup=$backup"
echo "secret_printed=false"
echo "PGBOUNCER_MESSAGING_RUNTIME_REFRESH_PASS"
