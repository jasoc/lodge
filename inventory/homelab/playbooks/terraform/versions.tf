# Every provider any module may use, pinned once. Always part of a generated root (a
# provider that's declared but unused is never configured, so it costs nothing).
terraform {
  required_providers {
    proxmox = {
      source  = "bpg/proxmox"
      version = "0.111.1"
    }
    cloudflare = {
      source  = "cloudflare/cloudflare"
      version = "5.23.0"
    }
  }
}
