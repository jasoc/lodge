# One Proxmox VM, cloned from an already-downloaded cloud image (see proxmox-image).
# lodge-import: proxmox_virtual_environment_vm.this
# import_id format: <node>/<vm_id>, e.g. proxmox/100

terraform {
  required_providers {
    proxmox = {
      source = "bpg/proxmox"
    }
  }
}

variable "name" {
  type = string
}

variable "node" {
  type    = string
  default = "proxmox"
}

variable "cores" {
  type = number
}

variable "cpu_type" {
  type    = string
  default = "x86-64-v3"
}

variable "memory_mb" {
  type = number
}

variable "disk_gb" {
  type = number
}

variable "image" {
  description = "Volume id of the cloud image to import, e.g. vault:import/<file>.qcow2."
  type        = string
}

variable "datastore" {
  type    = string
  default = "local-lvm"
}

variable "bridge" {
  type    = string
  default = "vmbr0"
}

variable "ip" {
  description = "CIDR, e.g. 192.168.178.112/24."
  type        = string
}

variable "gateway" {
  type = string
}

variable "super_admin" {
  type    = string
  default = "parisius"
}

# pass: SSH_PUBLIC_KEY
variable "ssh_public_key" {
  description = "Public SSH key injected via cloud-init."
  type        = string
}

resource "proxmox_virtual_environment_vm" "this" {
  name            = var.name
  node_name       = var.node
  stop_on_destroy = true

  agent {
    enabled = true
  }

  cpu {
    cores = var.cores
    type  = var.cpu_type
  }

  memory {
    dedicated = var.memory_mb
  }

  disk {
    datastore_id = var.datastore
    import_from  = var.image
    interface    = "virtio0"
    iothread     = true
    discard      = "on"
    size         = var.disk_gb
  }

  network_device {
    bridge = var.bridge
  }

  operating_system {
    type = "l26"
  }

  initialization {
    ip_config {
      ipv4 {
        address = var.ip
        gateway = var.gateway
      }
    }

    user_account {
      username = var.super_admin
      keys     = [trimspace(var.ssh_public_key)]
    }
  }
}

output "vm_id" {
  value = proxmox_virtual_environment_vm.this.vm_id
}
