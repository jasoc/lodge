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
  label: string;
  policy: 'AUTO' | 'MANUAL_REQUIRED' | 'OPTIONAL';
  requires: string | null;
  executor: 'container' | 'http';
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
  label: string | null;
  rules: SignalRuleModel[];
}

export interface CapabilityDefinitionModel {
  code: string;
  title: string;
  description: string;
  /** Signals and no rules: rendered as a card from the inventory, never reconciled. */
  is_view: boolean;
  signals: SignalDefinitionModel[];
}

export interface CapabilityCatalogModel {
  kind_code: string;
  capabilities: CapabilityDefinitionModel[];
}

/** Mirrors `Lodge.Core.Reconciliation.CapabilityViewData` — a view capability rendered
 * against one instance's inventory. */
export interface ViewColumnModel {
  label: string;
  field: string;
}

export interface ViewRowModel {
  key: string;
  values: (string | null)[];
}

export interface ViewTableModel {
  collection: string;
  columns: ViewColumnModel[];
  rows: ViewRowModel[];
}

export interface ViewValueModel {
  label: string;
  path: string;
  value: string | null;
}

export interface CapabilityViewModel {
  code: string;
  title: string;
  description: string;
  tables: ViewTableModel[];
  values: ViewValueModel[];
}
