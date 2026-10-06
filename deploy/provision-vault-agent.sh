#!/usr/bin/env bash
set -Eeuo pipefail

release_dir="${1:?release directory is required}"
app_user="xanhnow"
config_root="/etc/xanhnow/s101/messaging"
secret_root="/srv/xanhnow/s101/secrets/messaging"
service_name="xanhnow-messaging-vault-agent.service"

fail() {
    echo "status=FAIL"
    echo "reason=$1"
    exit 1
}

id "$app_user" >/dev/null 2>&1 || fail "app_user_missing"
command -v vault >/dev/null 2>&1 || fail "vault_binary_missing"
test -r /etc/xanhnow/s101/security/trust/vault-ca.crt || fail "vault_ca_missing"
test -r "$config_root/vault/role_id" || fail "approle_role_id_missing"
test -r "$config_root/vault/secret_id" || fail "approle_secret_id_missing"
test -r "$release_dir/deploy/vault-agent/vault-agent.hcl" || fail "vault_agent_config_missing"
test -d "$release_dir/deploy/vault-agent/templates" || fail "vault_templates_missing"

install -d -o root -g "$app_user" -m 0750 "$config_root/templates"
install -d -o "$app_user" -g "$app_user" -m 0700 "$secret_root"
install -o root -g "$app_user" -m 0640 \
    "$release_dir/deploy/vault-agent/vault-agent.hcl" \
    "$config_root/vault-agent.hcl"
find "$release_dir/deploy/vault-agent/templates" -maxdepth 1 -type f -name '*.ctmpl' -print0 |
    while IFS= read -r -d '' template; do
        install -o root -g "$app_user" -m 0640 "$template" "$config_root/templates/$(basename "$template")"
    done
install -o root -g root -m 0644 \
    "$release_dir/deploy/xanhnow-messaging-vault-agent.service" \
    "/etc/systemd/system/$service_name"

systemctl daemon-reload
systemctl enable "$service_name" >/dev/null
systemctl restart "$service_name"

for _ in {1..30}; do
    if sudo -u "$app_user" test -s "$secret_root/postgres-connection-string" &&
       sudo -u "$app_user" test -s "$secret_root/security-boundary-api-key" &&
       sudo -u "$app_user" test -s "$secret_root/redis-configuration" &&
       sudo -u "$app_user" test -s "$secret_root/redis-password" &&
       sudo -u "$app_user" test -s "$secret_root/kafka-password" &&
       sudo -u "$app_user" test -s "$secret_root/device-token-encryption-key"; then
        break
    fi
    sleep 1
done

systemctl is-active --quiet "$service_name" || fail "vault_agent_inactive"
sudo -u "$app_user" test -s "$secret_root/device-token-encryption-key" || fail "vault_templates_not_rendered"
echo "status=PASS"
echo "secret_printed=false"
echo "XANHNOW_MESSAGING_VAULT_AGENT_PASS"
