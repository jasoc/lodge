# lodge-provider-source: bpg/proxmox
# Included in a generated root when its module uses bpg/proxmox.

# pass: PROXMOX_VE_ENDPOINT
# pass: PROXMOX_VE_API_TOKEN
provider "proxmox" {
  insecure = true

  # run.sh starts an ssh-agent holding the homelab key (from Proton Pass).
  ssh {
    agent    = true
    username = "root"
  }
}
