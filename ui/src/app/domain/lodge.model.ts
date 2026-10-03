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
  action_key: string;
  runbook_ref: string;
  trigger: string;
  label: string;
  policy: 'AUTO' | 'MANUAL_REQUIRED' | 'OPTIONAL';
  status: 'QUEUED' | 'RUNNING' | 'SUCCEEDED' | 'FAILED' | 'SUPERSEDED';
  synthetic: boolean;
  pending_prompts: PendingPromptModel[];
  created_at: string;
  completed_at: string | null;
  /** Id of the latest run; null until the action has run. */
  execution_ref: string | null;
}

/** A slice of an action's latest run log — poll again from `next_offset` while `running`. */
export interface ActionLogModel {
  action_id: string;
  run_id: string | null;
  action_status: string;
  /** False when the action never ran or its executor keeps no local log (webhooks). */
  available: boolean;
  running: boolean;
  message: string | null;
  text: string;
  next_offset: number;
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

/** An `ActionModel` joined with the instance it belongs to — the row shape for the
 * cross-instance Drift/Actions pages. */
export interface GlobalActionModel extends ActionModel {
  kind_code: string;
  instance_id: string;
  instance_code: string;
  instance_display_name: string;
}

/** An `AuditEventModel` joined with the instance it belongs to, when it has one — the row
 * shape for the cross-instance Audit Log page. */
export interface GlobalAuditEventModel {
  id: string;
  kind_code: string;
  event_type: string;
  actor: string;
  payload_json: string | null;
  created_at: string;
  instance_id: string | null;
  instance_code: string | null;
  instance_display_name: string | null;
}

export interface CycleSummaryModel {
  kinds_checked: number;
  instances_reconciled: number;
  drift_count: number;
  messages: string[];
  validation_errors: string[];
}
