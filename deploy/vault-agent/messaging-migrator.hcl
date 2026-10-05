pid_file = "/srv/xanhnow/s101/secrets/messaging-migrator/vault-agent.pid"

vault {
  address = "https://192.168.2.81:8200"
  ca_cert = "/etc/xanhnow/s101/security/trust/vault-ca.crt"
}

auto_auth {
  method "approle" {
    mount_path = "auth/approle"
    config = {
      role_id_file_path = "/etc/xanhnow/s101/messaging/vault/migrator-role_id"
      secret_id_file_path = "/etc/xanhnow/s101/messaging/vault/migrator-secret_id"
      remove_secret_id_file_after_reading = false
    }
  }

  sink "file" {
    config = {
      path = "/srv/xanhnow/s101/secrets/messaging-migrator/.vault-token"
      mode = 0600
    }
  }
}

template {
  source = "/etc/xanhnow/s101/messaging/templates/postgres-migration-connection-string.ctmpl"
  destination = "/srv/xanhnow/s101/secrets/messaging-migrator/postgres-migration-connection-string"
  perms = "0600"
}
