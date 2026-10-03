-- Explicit executor selection (replaces runbook-alias-based inference) and unresolved
-- secret references, threaded from the catalog through the reconciler to
-- ActionExecutionService, which is the only place they're ever resolved to plaintext.
ALTER TABLE actions
    ADD COLUMN IF NOT EXISTS executor_kind        varchar(32) NOT NULL DEFAULT 'Shell', -- Shell | Webhook | Docker (Octopus | Kubernetes reserved)
    ADD COLUMN IF NOT EXISTS executor_config_json  text NULL, -- DockerExecutorConfig JSON, only when executor_kind = 'Docker'
    ADD COLUMN IF NOT EXISTS secret_inputs_json     text NULL; -- name -> secret ref map, unresolved, mirrors pending_prompts_json
