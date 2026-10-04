# One cloud image downloaded onto a Proxmox datastore (what VMs import their disk from).
# lodge-import: proxmox_download_file.this
# import_id format (bpg/proxmox): <node>/<datastore>:<content_type>/<file name>

terraform {
  required_providers {
    proxmox = {
      source = "bpg/proxmox"
    }
  }
}

variable "node" {
  type    = string
  default = "proxmox"
}

variable "datastore" {
  type = string
}

variable "url" {
  type = string
}

variable "content_type" {
  type    = string
  default = "import"
}

variable "file_name" {
  description = "Defaults to the URL's file name."
  type        = string
  default     = null
}

resource "proxmox_download_file" "this" {
  content_type = var.content_type
  datastore_id = var.datastore
  node_name    = var.node
  url          = var.url
  file_name    = var.file_name
  overwrite    = false

  # Same as the homelab repo: a downloaded image is never re-fetched or replaced.
  lifecycle {
    ignore_changes = all
  }
}

output "id" {
  description = "Volume id to use as a VM's `image`."
  value       = proxmox_download_file.this.id
}
