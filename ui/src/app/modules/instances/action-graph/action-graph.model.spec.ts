import { ActionModel } from '../../../domain';
import { buildActionGraph } from './action-graph.model';

let seq = 0;
function row(
  partial: Partial<ActionModel> & Pick<ActionModel, 'action_key' | 'status'>,
): ActionModel {
  seq++;
  return {
    id: `id-${seq}`,
    capability_code: 'vms',
    signal_path: 'proxmox.vms',
    item_key: 'vm1',
    requires: null,
    trigger: 'ADD',
    label: partial.action_key,
    policy: 'MANUAL_REQUIRED',
    synthetic: false,
    pending_prompts: [],
    created_at: '2026-01-01T00:00:00Z',
    completed_at: null,
    execution_ref: null,
    invalidated_at: null,
    depends_on: [],
    ...partial,
  };
}

const dep = (action_key: string, signal_path = 'proxmox.vms', item_key: string | null = 'vm1') => ({
  signal_path,
  item_key,
  action_key,
});

describe('buildActionGraph', () => {
  it('links a change to its first action and chains the rest through depends_on', () => {
    const rows = [
      row({
        action_key: 'deploy',
        status: 'BLOCKED',
        signal_path: 'proxmox.vms.*.stacks',
        item_key: 'vm1/web',
        depends_on: [dep('configure')],
      }),
      row({
        action_key: 'configure',
        status: 'BLOCKED',
        policy: 'AUTO',
        depends_on: [dep('apply')],
      }),
      row({ action_key: 'apply', status: 'QUEUED' }),
    ];

    const graph = buildActionGraph(rows);

    const edges = graph.edges
      .map((e) => `${e.from.split('::').pop()}->${e.to.split('::').pop()}`)
      .sort();
    expect(edges).toEqual(['ADD->apply', 'ADD->deploy', 'apply->configure', 'configure->deploy']);
    expect(
      graph.nodes
        .filter((n) => n.kind === 'cause')
        .map((n) => n.title)
        .sort(),
    ).toEqual(['vm1', 'vm1/web']);
    expect(graph.nodes.find((n) => n.title === 'deploy')!.waitingFor).toEqual(['configure (vm1)']);
  });

  it('prefers the live row over an older success and skips superseded rows', () => {
    const rows = [
      row({ action_key: 'apply', status: 'QUEUED' }),
      row({ action_key: 'apply', status: 'SUCCEEDED' }),
      row({ action_key: 'old', status: 'SUPERSEDED' }),
    ];

    const graph = buildActionGraph(rows);

    const actions = graph.nodes.filter((n) => n.kind === 'action');
    expect(actions.map((n) => `${n.title}:${n.action!.status}`)).toEqual(['apply:QUEUED']);
  });

  it('shows OPTIONAL actions with their last run, without keeping a settled group open', () => {
    const rows = [
      row({ action_key: 'plan', status: 'QUEUED', policy: 'OPTIONAL' }),
      row({ action_key: 'plan', status: 'SUCCEEDED', policy: 'OPTIONAL' }),
      row({ action_key: 'apply', status: 'QUEUED' }),
      row({ action_key: 'plan', status: 'QUEUED', policy: 'OPTIONAL', item_key: 'idle' }),
    ];

    const graph = buildActionGraph(rows);

    const plan = graph.nodes.find((n) => n.title === 'plan' && n.subtitle === 'vm1')!;
    expect(plan.optional).toBe(true);
    expect(plan.lastRun?.status).toBe('SUCCEEDED');
    expect(graph.nodes.some((n) => n.subtitle === 'idle')).toBe(false);
    expect(buildActionGraph(rows, true).nodes.some((n) => n.subtitle === 'idle')).toBe(true);
  });

  it('shows a dependency nothing scheduled as a ghost', () => {
    const graph = buildActionGraph([
      row({ action_key: 'configure', status: 'BLOCKED', depends_on: [dep('apply')] }),
    ]);
    expect(graph.nodes.find((n) => n.kind === 'ghost')?.title).toBe('apply');
  });

  it('hides fully settled groups unless asked', () => {
    const rows = [
      row({ action_key: 'apply', status: 'SUCCEEDED', item_key: 'done' }),
      row({ action_key: 'apply', status: 'QUEUED', item_key: 'todo' }),
    ];
    expect(
      buildActionGraph(rows)
        .nodes.filter((n) => n.kind === 'action')
        .map((n) => n.subtitle),
    ).toEqual(['todo']);
    expect(buildActionGraph(rows, true).nodes.filter((n) => n.kind === 'action')).toHaveLength(2);
  });
});
