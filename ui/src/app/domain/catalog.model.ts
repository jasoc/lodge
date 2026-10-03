/** Mirrors `Lodge.Server.Contracts` capability catalog DTOs — read-only view of a kind's
 * parsed capability YAML, with no instance context. Backs the Kinds/Capabilities browser. */

export interface RuleInputModel {
  name: string;
  kind: 'From' | 'Const' | 'Prompt';
  value: string | null;
  required: boolean;
}

export interface ActionTemplateModel {
  key: string;
  runbook: string;
  label: string;
  policy: 'AUTO' | 'MANUAL_REQUIRED' | 'OPTIONAL';
  inputs: RuleInputModel[];
  depends_on: string[];
}

export interface SignalRuleModel {
  trigger: 'STATE' | 'ADD' | 'DELETE' | 'MODIFY';
  when_json: string | null;
  item_key: string | null;
  actions: ActionTemplateModel[];
}

export interface SignalDefinitionModel {
  path: string;
  kind: 'Scalar' | 'KeyedCollection' | 'ScalarList';
  rules: SignalRuleModel[];
}

export interface CapabilityDefinitionModel {
  code: string;
  title: string;
  description: string;
  signals: SignalDefinitionModel[];
}

export interface CapabilityCatalogModel {
  kind_code: string;
  capabilities: CapabilityDefinitionModel[];
}
