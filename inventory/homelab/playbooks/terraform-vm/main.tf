# Pretend VM: a real Terraform run, but the "VM" is a terraform_data resource holding the
# spec a real hypervisor provider (bpg/proxmox, libvirt, ...) would receive. Swap the
# resource for the real one and keep the variables.

variable "vm_name" {
  type = string
}

variable "vm" {
  type = object({
    cpu_cores = number
    memory_mb = number
    disk_gb   = number
    os        = string
    network = object({
      bridge     = string
      ip_address = string
      gateway    = optional(string)
    })
    start_on_boot = optional(bool, true)
  })
}

resource "terraform_data" "vm" {
  input = {
    name       = var.vm_name
    cores      = var.vm.cpu_cores
    memory     = var.vm.memory_mb
    disk       = "${var.vm.disk_gb}G"
    template   = var.vm.os
    bridge     = var.vm.network.bridge
    ipconfig0  = var.vm.network.ip_address == "dhcp" ? "ip=dhcp" : "ip=${var.vm.network.ip_address},gw=${var.vm.network.gateway}"
    onboot     = var.vm.start_on_boot
  }
}

output "vm" {
  value = terraform_data.vm.output
}
