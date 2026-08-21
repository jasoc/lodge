/** Mirrors `Lodge.Server.Contracts.Dtos` — snake_case to match the server's JSON. */

export interface KindModel {
  code: string;
  name: string;
  enabled: boolean;
}

export interface InstanceModel {
  id: string;
  kind_code: string;
  instance_code: string;
  display_name: string;
  generation: number;
  region: string;
  created_at: string;
  last_revision_at: string | null;
}

export interface InstanceDetailModel {
  instance: InstanceModel;
  latest_yaml: string | null;
}

export interface PendingPromptModel {
  name: string;
  prompt: string;
  required: boolean;
}

export interface ActionModel {
  id: string;
  capability_code: string;
  signal_path: string;
  item_key: string | null;
  runbook_ref: string;
  trigger: string;
  label: string;
  policy: 'AUTO' | 'MANUAL_REQUIRED' | 'OPTIONAL';
  status: 'QUEUED' | 'RUNNING' | 'SUCCEEDED' | 'FAILED' | 'SUPERSEDED';
  synthetic: boolean;
  pending_prompts: PendingPromptModel[];
  created_at: string;
  completed_at: string | null;
}

export interface ActionExecutionResultModel {
  action_id: string;
  status: string;
  execution_ref: string | null;
  message: string | null;
  denied: boolean;
}

export interface AuditEventModel {
  id: string;
  event_type: string;
  actor: string;
  payload_json: string | null;
  created_at: string;
}

export interface CycleSummaryModel {
  kinds_checked: number;
  instances_reconciled: number;
  drift_count: number;
  messages: string[];
  validation_errors: string[];
}
