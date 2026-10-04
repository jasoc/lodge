# One Cloudflare DNS record.
# lodge-import: cloudflare_dns_record.this
# import_id format (cloudflare v5): <zone_id>/<record_id>

terraform {
  required_providers {
    cloudflare = {
      source = "cloudflare/cloudflare"
    }
  }
}

variable "zone_id" {
  type = string
}

variable "type" {
  type = string
}

variable "name" {
  type = string
}

variable "content" {
  type = string
}

variable "ttl" {
  description = "1 = automatic."
  type        = number
  default     = 1
}

variable "proxied" {
  type    = bool
  default = false
}

variable "priority" {
  description = "MX only."
  type        = number
  default     = null
}

resource "cloudflare_dns_record" "this" {
  zone_id  = var.zone_id
  name     = var.name
  type     = var.type
  content  = var.content
  ttl      = var.ttl
  proxied  = var.proxied
  priority = var.priority
}
