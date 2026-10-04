import { ActivatedRoute, RouterModule } from '@angular/router';

import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatRippleModule } from '@angular/material/core';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatTabsModule } from '@angular/material/tabs';

import { DynamicFormComponent } from '../../components/dynamic-form/dynamic-form.component';
import { DynamicFormRoot } from '../../components/dynamic-form/types/dynamic-form';
import { TextboxElement } from '../../components/dynamic-form/types/dynamic-form-element-textbox';
import { YamlViewerComponent } from '../../components/yaml-viewer/yaml-viewer.component';
import {
  ActionModel,
  CapabilityCatalogModel,
  CapabilityViewModel,
  InstanceDetailModel,
} from '../../domain';
import { AuthService } from '../../services/auth.service';
import { autoRefresh } from '../../services/auto-refresh';
import { LodgeService } from '../../services/lodge.service';
import { ActionGraphComponent } from './action-graph/action-graph.component';
import { identityKey } from './action-graph/action-graph.model';
import { RunLogDialogComponent, RunLogDialogData } from './run-log-dialog/run-log-dialog.component';
import { RunQueueComponent, RunRequest } from './run-queue/run-queue.component';
import { ViewCardComponent } from './view-card/view-card.component';

/**
 * One OPTIONAL identity (a check, a plan, a test): re-invocable, one row per execution.
 * `live` is the row the Run button acts on (QUEUED/FAILED, or RUNNING while it runs);
 * `last` is the most recent finished run, for the result badge and its log.
 */
interface OptionalEntry {
  key: string;
  live: ActionModel | null;
  last: ActionModel | null;
  label: string;
  item_key: string | null;
  signal_path: string;
}

/**
 * One capability's actions, split by what a human can do with them. Every list holds the
 * latest row per (signal_path, item_key, action_key) identity, except `history`.
 * - `todo`: waiting on a human — MANUAL_REQUIRED queued, anything FAILED (Retry), AUTO
 *   held by prompts.
 * - `optional`: OPTIONAL identities — always runnable again, shown as checks, not chores.
 * - `applied`: currently-valid successes — at most revocable.
 * - `running`: in flight right now, or AUTO about to start (also in the page-wide strip).
 * - `waiting`: BLOCKED — emitted already, held until their dependencies succeed.
 * - `history`: every row, newest first.
 */
interface CapabilityCard {
  code: string;
  title: string;
  todo: ActionModel[];
  optional: OptionalEntry[];
  applied: ActionModel[];
  running: ActionModel[];
  waiting: ActionModel[];
  history: ActionModel[];
}

type CardSection = 'todo' | 'waiting' | 'optional' | 'applied' | 'history';

/**
 * Consecutive rows of one section that belong to the same collection item, so a card with
 * many items reads item by item instead of repeating the key on every row. `item` is null
 * for actions on a plain (non-collection) signal; for a nested item (`vm/file`) `parent`
 * is the outer key and `name` the inner one.
 */
interface ItemGroup<T> {
  item: string | null;
  parent: string | null;
  name: string;
  rows: T[];
}

/** How many rows a section shows before "Show all". */
const SECTION_PREVIEW = 6;

@Component({
  selector: 'lodge-instance-detail',
  standalone: true,
  templateUrl: './instance-detail.component.html',
  styleUrls: ['./instance-detail.component.scss'],
  imports: [
    MatButtonModule,
    MatIconModule,
    MatChipsModule,
    MatRippleModule,
    MatSnackBarModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatTabsModule,
    DynamicFormComponent,
    YamlViewerComponent,
    ActionGraphComponent,
    RunQueueComponent,
    ViewCardComponent,
    RouterModule,
    NgTemplateOutlet,
    DatePipe,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class InstanceDetailComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly lodgeService = inject(LodgeService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly dialog = inject(MatDialog);
  readonly authService = inject(AuthService);

  // 'kind' belongs to the parent route (see app.routes.ts — nested one level per URL
  // segment, on purpose, so the breadcrumb trail shows each segment separately).
  readonly kindCode = this.route.snapshot.parent!.paramMap.get('kind')!;
  readonly instanceCode = this.route.snapshot.paramMap.get('instance')!;

  readonly loading = signal(true);
  readonly instance = signal<InstanceDetailModel | null>(null);
  readonly actions = signal<ActionModel[]>([]);
  /** The last attempt to load the actions failed (server error): what's shown may be stale. */
  readonly actionsFailed = signal(false);
  readonly capabilities = signal<CapabilityCatalogModel | null>(null);
  readonly views = signal<CapabilityViewModel[]>([]);
  /** Free-text filter over label / item key / action key, across every card. */
  readonly filter = signal('');
  /** Per card+section open/closed overrides; unset falls back to the section default. */
  private readonly sectionOpen = signal<Record<string, boolean>>({});
  /** Card+section pairs expanded past the preview length. */
  private readonly sectionExpanded = signal<Record<string, boolean>>({});

  /** Action id currently showing its pending-prompts form, if any. */
  readonly promptingActionId = signal<string | null>(null);
  readonly promptValues = signal<Record<string, any>>({});
  readonly busyActionId = signal<string | null>(null);
  readonly reconciling = signal(false);

  readonly hasActivity = computed(() =>
    this.actions().some(
      (a) => a.status === 'RUNNING' || (a.status === 'QUEUED' && a.policy === 'AUTO'),
    ),
  );

  /** Every action in flight right now, plus AUTO ones about to start — the page-wide strip. */
  readonly activeActions = computed(() =>
    this.actions()
      .filter((a) => a.status === 'RUNNING' || this.isStarting(a))
      .sort((a, b) => (a.status === b.status ? 0 : a.status === 'RUNNING' ? -1 : 1)),
  );

  readonly capabilityCards = computed<CapabilityCard[]>(() => {
    const catalog = this.capabilities();
    const titleByCode = new Map(catalog?.capabilities.map((c) => [c.code, c.title]) ?? []);
    const needle = this.filter().trim().toLowerCase();
    const matches = (a: ActionModel) =>
      !needle ||
      a.label.toLowerCase().includes(needle) ||
      a.action_key.toLowerCase().includes(needle) ||
      (a.item_key ?? '').toLowerCase().includes(needle);

    const groups = new Map<string, ActionModel[]>();
    for (const action of this.actions()) {
      const list = groups.get(action.capability_code) ?? [];
      list.push(action);
      groups.set(action.capability_code, list);
    }

    const cards: CapabilityCard[] = [];
    for (const [code, actions] of groups) {
      const visible = actions.filter(matches);
      if (visible.length === 0) {
        continue;
      }

      // Rows arrive newest first; the first seen per identity is its latest.
      const latest = new Map<string, ActionModel>();
      const optional = new Map<string, OptionalEntry>();
      for (const action of visible) {
        const key = this.identity(action);
        if (action.policy === 'OPTIONAL') {
          const entry = optional.get(key) ?? {
            key,
            live: null,
            last: null,
            label: action.label,
            item_key: action.item_key,
            signal_path: action.signal_path,
          };
          if (!entry.live && ['QUEUED', 'FAILED', 'RUNNING'].includes(action.status)) {
            entry.live = action;
          }
          if (!entry.last && (action.status === 'SUCCEEDED' || action.status === 'FAILED')) {
            entry.last = action;
          }
          optional.set(key, entry);
        } else if (!latest.has(key)) {
          latest.set(key, action);
        }
      }

      const todo: ActionModel[] = [];
      const applied: ActionModel[] = [];
      const running: ActionModel[] = [];
      const waiting: ActionModel[] = [];
      for (const action of latest.values()) {
        if (action.status === 'RUNNING' || this.isStarting(action)) {
          running.push(action);
        } else if (action.status === 'BLOCKED') {
          waiting.push(action);
        } else if (action.status === 'QUEUED' || action.status === 'FAILED') {
          todo.push(action);
        } else if (action.status === 'SUCCEEDED' && !action.invalidated_at) {
          applied.push(action);
        }
      }
      // An OPTIONAL identity with no live row is one the inventory no longer calls for
      // (its item was removed): only its history remains, nothing to run.
      for (const [key, entry] of optional) {
        if (!entry.live) {
          optional.delete(key);
        } else if (entry.live.status === 'RUNNING') {
          running.push(entry.live);
        }
      }

      // Failures first — they're the ones that went wrong, not just the ones waiting.
      todo.sort((a, b) => Number(b.status === 'FAILED') - Number(a.status === 'FAILED'));
      applied.sort((a, b) =>
        (b.completed_at ?? b.created_at).localeCompare(a.completed_at ?? a.created_at),
      );

      cards.push({
        code,
        title: titleByCode.get(code) ?? code,
        todo,
        optional: Array.from(optional.values()),
        applied,
        running,
        waiting,
        history: visible,
      });
    }

    // Cards that need a human float to the top.
    return cards.sort((a, b) => Number(b.todo.length > 0) - Number(a.todo.length > 0));
  });

  constructor() {
    this.load();
    // Fast while a run is in flight (or an AUTO action is about to start), so status
    // changes land within seconds; a relaxed pace once everything has settled.
    autoRefresh(
      () => this.load({ silent: true }),
      () => (this.hasActivity() ? 2000 : 10000),
    );
  }

  async load(opts: { silent?: boolean } = {}) {
    if (!opts.silent) {
      this.loading.set(true);
    }
    try {
      const [instance, actions, capabilities, views] = await Promise.all([
        this.lodgeService.getInstance(this.kindCode, this.instanceCode),
        // A failing action list must not blank the page: keep the last good one and say so.
        this.lodgeService.getActions(this.kindCode, this.instanceCode).catch(() => null),
        this.lodgeService.getCapabilities(this.kindCode),
        // Views are decoration: a failure there must not take the action cards down.
        this.lodgeService.getViews(this.kindCode, this.instanceCode).catch(() => []),
      ]);
      this.instance.set(instance);
      this.actionsFailed.set(actions === null);
      if (actions !== null) {
        this.actions.set(actions);
      }
      this.capabilities.set(capabilities);
      this.views.set(views);
    } finally {
      this.loading.set(false);
    }
  }

  promptForm(action: ActionModel): DynamicFormRoot {
    return action.pending_prompts.map(
      (p, i) =>
        new TextboxElement({
          key: p.name,
          label: p.prompt,
          required: p.required,
          order: i,
        }),
    );
  }

  startConfirm(action: ActionModel) {
    if (action.pending_prompts.length > 0) {
      this.promptValues.set({});
      this.promptingActionId.set(action.id);
      return;
    }
    this.confirm(action, {});
  }

  /** From the run queue: start it and stay on the list, the log is one click away. */
  runFromQueue({ action, prompts }: RunRequest) {
    return this.confirm(action, prompts, { followLog: false });
  }

  async confirm(
    action: ActionModel,
    prompts: Record<string, any>,
    { followLog = true }: { followLog?: boolean } = {},
  ) {
    this.busyActionId.set(action.id);
    this.promptingActionId.set(null);
    try {
      const result = await this.lodgeService.confirmAction(
        this.kindCode,
        this.instanceCode,
        action.id,
        prompts,
      );
      if (result.denied) {
        this.snackBar.open(`Denied: ${result.message}`, 'Close', { duration: 4000 });
      } else if (result.execution_ref && followLog) {
        // Follow the run live instead of a fire-and-forget toast.
        this.openLog({ ...action, status: 'RUNNING', execution_ref: result.execution_ref });
      } else if (result.execution_ref) {
        const running = {
          ...action,
          status: 'RUNNING' as const,
          execution_ref: result.execution_ref,
        };
        this.snackBar
          .open(`Started ${action.label}`, 'Logs', { duration: 4000 })
          .onAction()
          .subscribe(() => this.openLog(running));
      } else {
        this.snackBar.open(result.message ?? result.status, 'Close', { duration: 3000 });
      }
      await this.load({ silent: true });
    } finally {
      this.busyActionId.set(null);
    }
  }

  async invalidate(action: ActionModel) {
    this.busyActionId.set(action.id);
    try {
      const result = await this.lodgeService.invalidateAction(
        this.kindCode,
        this.instanceCode,
        action.id,
      );
      this.snackBar.open(result.message ?? result.status, 'Close', { duration: 3000 });
      await this.load({ silent: true });
    } finally {
      this.busyActionId.set(null);
    }
  }

  async reconcileNow() {
    this.reconciling.set(true);
    try {
      const summary = await this.lodgeService.reconcile();
      const errors = summary.validation_errors.length;
      this.snackBar.open(
        `Reconciled · ${summary.drift_count} drifting` +
          (errors ? ` · ${errors} validation error(s)` : ''),
        'Close',
        { duration: errors ? 6000 : 3000 },
      );
      await this.load({ silent: true });
    } catch {
      this.snackBar.open('Reconciliation failed — see the server log.', 'Close', {
        duration: 5000,
      });
    } finally {
      this.reconciling.set(false);
    }
  }

  openLog(action: ActionModel) {
    this.dialog
      .open<RunLogDialogComponent, RunLogDialogData>(RunLogDialogComponent, {
        data: { kindCode: this.kindCode, instanceCode: this.instanceCode, action },
        width: '960px',
        maxWidth: '95vw',
      })
      .afterClosed()
      .subscribe(() => void this.load({ silent: true }));
  }

  hasLog(action: ActionModel): boolean {
    return !!action.execution_ref;
  }

  canConfirm(action: ActionModel): boolean {
    return action.status === 'QUEUED' || action.status === 'FAILED';
  }

  canInvalidate(action: ActionModel): boolean {
    return action.status === 'SUCCEEDED' && !action.synthetic && !action.invalidated_at;
  }

  /** AUTO and unblocked: the loop starts it on its own within seconds. */
  isStarting(action: ActionModel): boolean {
    return (
      action.status === 'QUEUED' && action.policy === 'AUTO' && action.pending_prompts.length === 0
    );
  }

  statusColor(status: string): 'primary' | 'accent' | 'warn' {
    if (status === 'SUCCEEDED') return 'primary';
    if (status === 'FAILED') return 'warn';
    return 'accent';
  }

  isSectionOpen(card: CapabilityCard, section: CardSection): boolean {
    const override = this.sectionOpen()[`${card.code}:${section}`];
    if (override !== undefined) {
      return override;
    }
    // A filter is a search: open whatever it found.
    if (this.filter().trim()) {
      return section !== 'history';
    }
    return section === 'todo' || section === 'optional';
  }

  toggleSection(card: CapabilityCard, section: CardSection) {
    const key = `${card.code}:${section}`;
    this.sectionOpen.update((s) => ({ ...s, [key]: !this.isSectionOpen(card, section) }));
  }

  visibleRows<T>(card: CapabilityCard, section: CardSection, rows: T[]): T[] {
    return this.sectionExpanded()[`${card.code}:${section}`]
      ? rows
      : rows.slice(0, SECTION_PREVIEW);
  }

  hiddenCount(card: CapabilityCard, section: CardSection, rows: unknown[]): number {
    return this.sectionExpanded()[`${card.code}:${section}`]
      ? 0
      : Math.max(0, rows.length - SECTION_PREVIEW);
  }

  expandSection(card: CapabilityCard, section: CardSection) {
    this.sectionExpanded.update((s) => ({ ...s, [`${card.code}:${section}`]: true }));
  }

  /** Groups consecutive rows by collection item (rows arrive already in display order). */
  byItem<T extends { item_key: string | null }>(rows: T[]): ItemGroup<T>[] {
    const groups: ItemGroup<T>[] = [];
    const index = new Map<string, ItemGroup<T>>();
    for (const row of rows) {
      const key = row.item_key ?? '';
      let group = index.get(key);
      if (!group) {
        const slash = row.item_key?.indexOf('/') ?? -1;
        group = {
          item: row.item_key,
          parent: slash > 0 ? row.item_key!.slice(0, slash) : null,
          name: slash > 0 ? row.item_key!.slice(slash + 1) : (row.item_key ?? ''),
          rows: [],
        };
        index.set(key, group);
        groups.push(group);
      }
      group.rows.push(row);
    }
    // Plain-signal actions first, then items in their display order.
    return groups.sort((a, b) => Number(a.item !== null) - Number(b.item !== null));
  }

  /** {@link lockedReason} as a value, for the graph's and the queue's input. */
  readonly lockedReasonFn = (action: ActionModel) => this.lockedReason(action);

  readonly capabilityTitleFn = (code: string) =>
    this.capabilities()?.capabilities.find((c) => c.code === code)?.title ?? code;

  /** What a BLOCKED action still waits for: its dependencies without a valid success. */
  waitingFor(action: ActionModel): string[] {
    const done = (row: ActionModel) => row.status === 'SUCCEEDED' && !row.invalidated_at;
    // Per identity: a valid success if there is one, else its newest row (for the label).
    const latest = new Map<string, ActionModel>();
    for (const row of this.actions()) {
      const key = identityKey(row);
      const seen = latest.get(key);
      if (!seen || (done(row) && !done(seen))) {
        latest.set(key, row);
      }
    }
    return (action.depends_on ?? [])
      .filter((d) => {
        const row = latest.get(identityKey(d));
        return !row || !done(row);
      })
      .map((d) => {
        const label = latest.get(identityKey(d))?.label ?? d.action_key;
        return d.item_key && d.item_key !== action.item_key ? `${label} (${d.item_key})` : label;
      });
  }

  /** Why the current user can't run an action, or null when they can. */
  lockedReason(action: ActionModel): string | null {
    return this.authService.canRun(action.requires)
      ? null
      : `Only members of '${action.requires}' can run this`;
  }

  /** "3m ago" style, coarse on purpose — refreshed with every poll. */
  ago(iso: string | null): string {
    if (!iso) {
      return '';
    }
    const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
    if (seconds < 60) return 'just now';
    if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
    if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
    return `${Math.floor(seconds / 86400)}d ago`;
  }

  private identity(action: ActionModel): string {
    return `${action.signal_path}::${action.item_key ?? ''}::${action.action_key}`;
  }
}
