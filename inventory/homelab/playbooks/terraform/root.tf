# The fixed part of every generated root module (see run.sh): one Lodge unit — one
# inventory item — per root, with its own state: a workspace (named after the unit) of the
# homelab's Postgres state database. The backend reads its connection string from
# PG_CONN_STR, which tf-run.sh sets from the tf_pg_conn_str input (kind.yaml defaults).

terraform {
  required_version = "~> 1.15"

  backend "pg" {}
}
