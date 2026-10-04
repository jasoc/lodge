import { ActionModel } from '../../../domain';
import { buildRunQueue } from './run-queue.model';

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

const dep = (action_key: string, item_key: string | null = 'vm1') => ({
  signal_path: 'proxmox.vms',
  item_key,
  action_key,
});

describe('buildRunQueue', () => {
  it('orders by dependencies, the one that unblocks the most first among peers', () => {
    const rows = [
      row({ action_key: 'deploy', status: 'BLOCKED', depends_on: [dep('configure')] }),
      row({ action_key: 'configure', status: 'BLOCKED', depends_on: [dep('apply')] }),
      row({ action_key: 'lonely', status: 'QUEUED', item_key: 'vm2' }),
      row({ action_key: 'apply', status: 'QUEUED' }),
    ];

    const { queue } = buildRunQueue(rows);

    expect(queue.map((e) => `${e.action.action_key}:${e.depth}:${e.unblocks}`)).toEqual([
      'apply:0:2',
      'lonely:0:0',
      'configure:1:1',
      'deploy:2:0',
    ]);
    expect(queue[2].waitingFor).toEqual(['apply']);
  });

  it('puts running and self-starting actions aside, and leaves done ones out', () => {
    const rows = [
      row({ action_key: 'configure', status: 'BLOCKED', depends_on: [dep('apply')] }),
      row({ action_key: 'apply', status: 'RUNNING' }),
      row({ action_key: 'dns', status: 'QUEUED', policy: 'AUTO', item_key: 'vm2' }),
      row({ action_key: 'old', status: 'SUCCEEDED', item_key: 'vm3' }),
    ];

    const { active, queue } = buildRunQueue(rows);

    expect(active.map((e) => e.state)).toEqual(['running', 'starting']);
    expect(queue.map((e) => `${e.action.action_key}:${e.depth}`)).toEqual(['configure:1']);
  });

  it('lists optional actions on demand with their last run', () => {
    const rows = [
      row({ action_key: 'plan', status: 'QUEUED', policy: 'OPTIONAL' }),
      row({ action_key: 'plan', status: 'FAILED', policy: 'OPTIONAL' }),
    ];

    const { onDemand, queue } = buildRunQueue(rows);

    expect(queue).toHaveLength(0);
    expect(onDemand.map((e) => `${e.state}:${e.lastRun?.status}`)).toEqual(['ready:FAILED']);
  });
});
