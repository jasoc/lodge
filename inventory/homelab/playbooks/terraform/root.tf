# The fixed part of every generated root module (see run.sh): one Lodge unit — one
# inventory item — per root, with its own local state at /state/<unit>.tfstate.
terraform {
  required_version = "~> 1.15"

  backend "local" {}
}
