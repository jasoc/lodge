FROM hashicorp/terraform:1.9

WORKDIR /tf
COPY main.tf ./
# Only the built-in `terraform` provider is used, so init needs no network at run time.
RUN terraform init -input=false -no-color

COPY --chmod=0755 run.sh ./
ENTRYPOINT ["/tf/run.sh"]
