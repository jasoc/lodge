import { ActionModel } from '../../../domain';
import { identityKey, indexActions, LIVE } from '../action-graph/action-graph.model';

/**
 * What a queue entry needs to move on:
 * - `running`: in flight; `starting`: AUTO and unblocked, the loop picks it up on its own.
 * - `failed` / `ready`: a button to press (Retry / Run); `input`: the same, after prompts.
 * - `blocked`: held until its dependencies succeed.
 */
export type QueueState = 'running' | 'starting' | 'failed' | 'ready' | 'input' | 'blocked';

export interface QueueEntry {
  key: string;
  action: ActionModel;
  state: QueueState;
  /** Unfinished dependencies chained before it: 0 = nothing in its way. */
  depth: number;
  /** Queue entries that wait for this one, directly or not. */
  unblocks: number;
  /** For a blocked entry: the labels of what it's waiting for. */
  waitingFor: string[];
  /** For an optional entry: its most recent finished run, if any. */
  lastRun: ActionModel | null;
}

export interface RunQueue {
  /** In flight right now, or about to start on their own. */
  active: QueueEntry[];
  /** Every required action still to do, in dependency order: the first ones unblock the rest. */
  queue: QueueEntry[];
  /** OPTIONAL actions (plans, checks): run at will, never in anyone's way. */
  onDemand: QueueEntry[];
}

/** Within the same depth: something to press before something to wait for. */
const STATE_RANK: Record<QueueState, number> = {
  running: 0,
  starting: 1,
  failed: 2,
  ready: 3,
  input: 4,
  blocked: 5,
};

function stateOf(action: ActionModel): QueueState {
  switch (action.status) {
    case 'RUNNING':
      return 'running';
    case 'FAILED':
      return 'failed';
    case 'BLOCKED':
      return 'blocked';
    default:
      if (action.pending_prompts.length > 0) {
        return 'input';
      }
      return action.policy === 'AUTO' ? 'starting' : 'ready';
  }
}

/**
 * The instance's live actions as a to-do list an operator can work top-down: everything
 * that's not done yet, ordered so that each entry comes after what it depends on, and among
 * peers the one that unblocks the most comes first.
 *
 * @param rows every action row of the instance, newest first (as the API returns them).
 */
export function buildRunQueue(rows: ActionModel[]): RunQueue {
  const index = indexActions(rows);

  const live = new Map<string, ActionModel>();
  const onDemand: QueueEntry[] = [];
  for (const [key, action] of index.current) {
    if (action.policy === 'OPTIONAL') {
      onDemand.push(entry(key, action, 0, 0, index.lastRun.get(key) ?? null));
    } else if (LIVE.has(action.status)) {
      live.set(key, action);
    }
  }

  // Edges among live required actions only: a dependency already done isn't in the way.
  const dependents = new Map<string, string[]>();
  for (const [key, action] of live) {
    for (const dep of action.depends_on ?? []) {
      const depKey = identityKey(dep);
      if (live.has(depKey)) {
        dependents.set(depKey, [...(dependents.get(depKey) ?? []), key]);
      }
    }
  }

  const depth = new Map<string, number>();
  const visiting = new Set<string>();
  const depthOf = (key: string): number => {
    const known = depth.get(key);
    if (known !== undefined) {
      return known;
    }
    if (visiting.has(key)) {
      return 0; // a cycle: the server would never unblock it either, don't loop here
    }
    visiting.add(key);
    let d = 0;
    for (const dep of live.get(key)!.depends_on ?? []) {
      const depKey = identityKey(dep);
      if (live.has(depKey)) {
        d = Math.max(d, depthOf(depKey) + 1);
      }
    }
    visiting.delete(key);
    depth.set(key, d);
    return d;
  };

  const unblocksOf = (key: string): number => {
    const seen = new Set<string>();
    const stack = [...(dependents.get(key) ?? [])];
    while (stack.length) {
      const next = stack.pop()!;
      if (next !== key && !seen.has(next)) {
        seen.add(next);
        stack.push(...(dependents.get(next) ?? []));
      }
    }
    return seen.size;
  };

  const entries = Array.from(live, ([key, action]) =>
    entry(key, action, depthOf(key), unblocksOf(key), null),
  ).sort(
    (a, b) =>
      a.depth - b.depth ||
      Number(a.state === 'blocked') - Number(b.state === 'blocked') ||
      b.unblocks - a.unblocks ||
      STATE_RANK[a.state] - STATE_RANK[b.state] ||
      a.action.created_at.localeCompare(b.action.created_at),
  );

  const isActive = (e: QueueEntry) => e.state === 'running' || e.state === 'starting';
  return {
    active: [...entries.filter(isActive), ...onDemand.filter(isActive)],
    queue: entries.filter((e) => !isActive(e)),
    onDemand: onDemand
      .filter((e) => !isActive(e))
      .sort(
        (a, b) =>
          (a.action.item_key ?? '').localeCompare(b.action.item_key ?? '') ||
          a.action.label.localeCompare(b.action.label),
      ),
  };

  function entry(
    key: string,
    action: ActionModel,
    d: number,
    unblocks: number,
    lastRun: ActionModel | null,
  ): QueueEntry {
    const state = stateOf(action);
    return {
      key,
      action,
      state,
      depth: d,
      unblocks,
      waitingFor: state === 'blocked' ? index.waitingFor(action) : [],
      lastRun,
    };
  }
}
