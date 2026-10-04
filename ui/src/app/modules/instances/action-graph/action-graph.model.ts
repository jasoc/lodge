import { ActionIdentityModel, ActionModel } from '../../../domain';

/**
 * The action graph of one instance: what each inventory change set off, and what waits for
 * what. Built purely from the instance's action rows (`depends_on` are the edges), so it
 * shows exactly what the reconciler emitted.
 *
 * - `cause` nodes: one per changed item (signal path + item key + trigger) — ADD/MODIFY/
 *   DELETE of a VM, a stack file, a record...
 * - `action` nodes: the current row of each identity (see {@link indexActions}). OPTIONAL
 *   actions (plans, checks) are included as on-demand tools, flagged `optional`.
 * - `ghost` nodes: a dependency with no row at all (nothing scheduled it).
 *
 * Edges: dependency → dependent, and cause → action, the latter only when none of the
 * action's dependencies already comes from the same cause (so a VM's add points at its
 * apply, and configure hangs off apply instead of off the add a second time).
 */
export type GraphNodeKind = 'cause' | 'action' | 'ghost';

export interface GraphNode {
  id: string;
  kind: GraphNodeKind;
  title: string;
  subtitle: string | null;
  /** The row behind an `action` node. */
  action: ActionModel | null;
  /** An OPTIONAL action: re-runnable at will, never in anyone's way. */
  optional: boolean;
  /** For an optional action: its most recent finished run, if any. */
  lastRun: ActionModel | null;
  /** ADD/MODIFY/DELETE for a `cause` node. */
  trigger: string | null;
  /** For a BLOCKED action: the labels of what it's waiting for. */
  waitingFor: string[];
}

export interface GraphEdge {
  from: string;
  to: string;
  kind: 'cause' | 'dependency';
}

export interface ActionGraph {
  nodes: GraphNode[];
  edges: GraphEdge[];
}

export const LIVE = new Set(['BLOCKED', 'QUEUED', 'RUNNING', 'FAILED']);

export function identityKey(i: ActionIdentityModel): string {
  return `${i.signal_path}::${i.item_key ?? ''}::${i.action_key}`;
}

function causeKey(a: ActionModel): string {
  return `cause::${a.signal_path}::${a.item_key ?? ''}::${a.trigger}`;
}

/** The instance's rows reduced to one per identity, with what dependencies need. */
export interface ActionIndex {
  /**
   * The current row per identity. Required actions: the live row (BLOCKED, QUEUED, RUNNING,
   * FAILED) if there is one, else the latest valid success. OPTIONAL actions: the live row
   * only — one without is no longer called for by the inventory.
   */
  current: Map<string, ActionModel>;
  /** The newest finished (SUCCEEDED/FAILED) row per identity. */
  lastRun: Map<string, ActionModel>;
  /** Whether a dependency on this identity is satisfied. */
  isDone(key: string): boolean;
  /** What a BLOCKED action still waits for, as labels. */
  waitingFor(action: ActionModel): string[];
}

/** @param rows every action row of the instance, newest first (as the API returns them). */
export function indexActions(rows: ActionModel[]): ActionIndex {
  const current = new Map<string, ActionModel>();
  const lastRun = new Map<string, ActionModel>();
  const succeededOnce = new Set<string>();
  for (const row of rows) {
    const key = identityKey(row);
    const seen = current.get(key);
    if (LIVE.has(row.status)) {
      if (!seen || !LIVE.has(seen.status)) {
        current.set(key, row);
      }
    } else if (
      row.policy !== 'OPTIONAL' &&
      row.status === 'SUCCEEDED' &&
      !row.invalidated_at &&
      !seen
    ) {
      current.set(key, row);
    }
    if ((row.status === 'SUCCEEDED' || row.status === 'FAILED') && !lastRun.has(key)) {
      lastRun.set(key, row);
    }
    if (row.status === 'SUCCEEDED' && !row.invalidated_at) {
      succeededOnce.add(key);
    }
  }

  // An optional check is re-queued after every run: any valid success of it counts.
  const isDone = (key: string) => {
    const row = current.get(key);
    return row?.status === 'SUCCEEDED' || (row?.policy === 'OPTIONAL' && succeededOnce.has(key));
  };
  // The item is only named when it isn't the waiting action's own.
  const labelOf = (dep: ActionIdentityModel, ownItem: string | null) => {
    const label = current.get(identityKey(dep))?.label ?? dep.action_key;
    return dep.item_key && dep.item_key !== ownItem ? `${label} (${dep.item_key})` : label;
  };
  const waitingFor = (action: ActionModel) =>
    (action.depends_on ?? [])
      .filter((d) => !isDone(identityKey(d)))
      .map((d) => labelOf(d, action.item_key));

  return { current, lastRun, isDone, waitingFor };
}

/**
 * @param rows every action row of the instance, newest first (as the API returns them).
 * @param includeSettled keep connected groups where everything already succeeded; by
 *   default only groups with something still to happen are shown.
 */
export function buildActionGraph(rows: ActionModel[], includeSettled = false): ActionGraph {
  const index = indexActions(rows);
  const { current } = index;

  const nodes = new Map<string, GraphNode>();
  const edges: GraphEdge[] = [];

  for (const [key, action] of current) {
    const deps = action.depends_on ?? [];
    const optional = action.policy === 'OPTIONAL';
    nodes.set(key, {
      id: key,
      kind: 'action',
      title: action.label,
      subtitle: action.item_key,
      action,
      optional,
      lastRun: optional ? (index.lastRun.get(key) ?? null) : null,
      trigger: null,
      waitingFor: action.status === 'BLOCKED' ? index.waitingFor(action) : [],
    });

    for (const dep of deps) {
      const depKey = identityKey(dep);
      if (!current.has(depKey) && !nodes.has(depKey)) {
        nodes.set(depKey, {
          id: depKey,
          kind: 'ghost',
          title: dep.action_key,
          subtitle: dep.item_key,
          action: null,
          optional: false,
          lastRun: null,
          trigger: null,
          waitingFor: [],
        });
      }
      edges.push({ from: depKey, to: key, kind: 'dependency' });
    }

    const cause = causeKey(action);
    const causedAlready = deps.some((d) => {
      const dep = current.get(identityKey(d));
      return !!dep && causeKey(dep) === cause;
    });
    if (!causedAlready) {
      if (!nodes.has(cause)) {
        nodes.set(cause, {
          id: cause,
          kind: 'cause',
          title: action.item_key ?? action.signal_path,
          subtitle: action.item_key ? action.signal_path : null,
          action: null,
          optional: false,
          lastRun: null,
          trigger: action.trigger,
          waitingFor: [],
        });
      }
      edges.push({ from: cause, to: key, kind: 'cause' });
    }
  }

  return includeSettled
    ? { nodes: Array.from(nodes.values()), edges }
    : withoutSettledGroups(Array.from(nodes.values()), edges);
}

/**
 * Drops connected groups with nothing left to happen: every required action succeeded and
 * no optional one is running or failed (an idle check doesn't keep a group on screen).
 */
function withoutSettledGroups(nodes: GraphNode[], edges: GraphEdge[]): ActionGraph {
  const parent = new Map(nodes.map((n) => [n.id, n.id]));
  const find = (id: string): string => {
    let root = id;
    while (parent.get(root) !== root) {
      root = parent.get(root)!;
    }
    parent.set(id, root);
    return root;
  };
  for (const e of edges) {
    parent.set(find(e.from), find(e.to));
  }

  const isOpen = (n: GraphNode) => {
    if (n.kind === 'ghost') {
      return true;
    }
    if (!n.action) {
      return false;
    }
    return n.optional
      ? n.action.status === 'RUNNING' || n.action.status === 'FAILED'
      : n.action.status !== 'SUCCEEDED';
  };
  const open = new Set(nodes.filter(isOpen).map((n) => find(n.id)));

  const keep = new Set(nodes.filter((n) => open.has(find(n.id))).map((n) => n.id));
  return {
    nodes: nodes.filter((n) => keep.has(n.id)),
    edges: edges.filter((e) => keep.has(e.from) && keep.has(e.to)),
  };
}
