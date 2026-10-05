pid_file = "/srv/xanhnow/s101/secrets/messaging/vault-agent.pid"

vault {
  address = "https://192.168.2.81:8200"
  ca_cert = "/etc/xanhnow/s101/security/trust/vault-ca.crt"
}

auto_auth {
  method "approle" {
    mount_path = "auth/approle"
    config = {
      role_id_file_path = "/etc/xanhnow/s101/messaging/vault/role_id"
      secret_id_file_path = "/etc/xanhnow/s101/messaging/vault/secret_id"
      remove_secret_id_file_after_reading = false
    }
  }

  sink "file" {
    config = {
      path = "/srv/xanhnow/s101/secrets/messaging/.vault-token"
      mode = 0600
    }
  }
}

template {
  source = "/etc/xanhnow/s101/messaging/templates/postgres-connection-string.ctmpl"
  destination = "/srv/xanhnow/s101/secrets/messaging/postgres-connection-string"
  perms = "0600"
}

template {
  source = "/etc/xanhnow/s101/messaging/templates/security-jwt-signing-key.ctmpl"
  destination = "/srv/xanhnow/s101/secrets/messaging/security-jwt-signing-key"
  perms = "0600"
}

template {
  source = "/etc/xanhnow/s101/messaging/templates/redis-configuration.ctmpl"
  destination = "/srv/xanhnow/s101/secrets/messaging/redis-configuration"
  perms = "0600"
}

template {
  source = "/etc/xanhnow/s101/messaging/templates/redis-password.ctmpl"
  destination = "/srv/xanhnow/s101/secrets/messaging/redis-password"
  perms = "0600"
}

template {
  source = "/etc/xanhnow/s101/messaging/templates/kafka-password.ctmpl"
  destination = "/srv/xanhnow/s101/secrets/messaging/kafka-password"
  perms = "0600"
}

template {
  source = "/etc/xanhnow/s101/messaging/templates/device-token-encryption-key.ctmpl"
  destination = "/srv/xanhnow/s101/secrets/messaging/device-token-encryption-key"
  perms = "0600"
}

