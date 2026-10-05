# Mock of the Terraform runner: a container that does nothing (see _base/mock-playbook.sh).
FROM busybox:1.37
COPY --from=base --chmod=0755 mock-playbook.sh /usr/local/bin/
ENTRYPOINT ["/usr/local/bin/mock-playbook.sh", "terraform"]
