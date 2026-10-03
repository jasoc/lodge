import { parse as parseYaml } from 'yaml';

import { ActivatedRoute, RouterModule } from '@angular/router';

import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';

import { DynamicFormComponent } from '../../components/dynamic-form/dynamic-form.component';
import { DynamicFormRoot } from '../../components/dynamic-form/types/dynamic-form';
import { TextboxElement } from '../../components/dynamic-form/types/dynamic-form-element-textbox';
import { M3TabComponent } from '../../components/m3-tabs/m3-tab/m3-tab.component';
import { M3TabsComponent } from '../../components/m3-tabs/m3-tabs.component';
import { ActionModel, CapabilityCatalogModel, InstanceDetailModel } from '../../domain';
import { autoRefresh } from '../../services/auto-refresh';
import { LodgeService } from '../../services/lodge.service';
import { RunLogDialogComponent, RunLogDialogData } from './run-log-dialog/run-log-dialog.component';

/**
 * One capability's actions, grouped for the control-panel card grid.
 * - `buttons`: one big tile per distinct (signal_path, item_key, action_key) identity for
 *   every MANUAL_REQUIRED/OPTIONAL action — always shown, whatever its current status
 *   (pending, running, already done). AUTO never needs a click, so it's kept out of here.
 * - `autoActions`: same dedup, for AUTO identities — a compact, collapsible list, since
 *   these run themselves.
 * - `history`: every action row for the capability, newest first — collapsible, off by
 *   default.
 */
interface CapabilityCard {
  code: string;
  title: string;
  buttons: ActionModel[];
  autoActions: ActionModel[];
  history: ActionModel[];
}

interface InventorySignalNode {
  path: string;
  value: unknown;
  isSignal: boolean;
}

interface InventoryNode {
  key: string;
  signals: InventorySignalNode[];
}

@Component({
  selector: 'lodge-instance-detail',
  standalone: true,
  templateUrl: './instance-detail.component.html',
  styleUrls: ['./instance-detail.component.scss'],
  imports: [
    MatButtonModule,
    MatIconModule,
    MatChipsModule,
    MatSnackBarModule,
    MatDialogModule,
    M3TabsComponent,
    M3TabComponent,
    DynamicFormComponent,
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

  // 'kind' belongs to the parent route (see app.routes.ts — nested one level per URL
  // segment, on purpose, so the breadcrumb trail shows each segment separately).
  readonly kindCode = this.route.snapshot.parent!.paramMap.get('kind')!;
  readonly instanceCode = this.route.snapshot.paramMap.get('instance')!;

  readonly loading = signal(true);
  readonly instance = signal<InstanceDetailModel | null>(null);
  readonly actions = signal<ActionModel[]>([]);
  readonly capabilities = signal<CapabilityCatalogModel | null>(null);
  readonly showHistory = signal(false);
  readonly showAutoActions = signal(false);

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

  /** Signal path clicked in the inventory canvas — highlights the matching button tile so
   * the canvas and the capability controls read as one fused view. */
  readonly highlightedSignalPath = signal<string | null>(null);

  readonly canvasOffsetX = signal(0);
  readonly canvasOffsetY = signal(0);
  readonly canvasScale = signal(1);
  private panning = false;
  private panStartX = 0;
  private panStartY = 0;

  readonly capabilityCards = computed<CapabilityCard[]>(() => {
    const catalog = this.capabilities();
    const titleByCode = new Map(catalog?.capabilities.map((c) => [c.code, c.title]) ?? []);

    const groups = new Map<string, ActionModel[]>();
    for (const action of this.actions()) {
      const list = groups.get(action.capability_code) ?? [];
      list.push(action);
      groups.set(action.capability_code, list);
    }

    return Array.from(groups.entries()).map(([code, actions]) => {
      const buttonsByIdentity = new Map<string, ActionModel>();
      const autoByIdentity = new Map<string, ActionModel>();
      for (const action of actions) {
        const target = action.policy === 'AUTO' ? autoByIdentity : buttonsByIdentity;
        const identity = `${action.signal_path}::${action.item_key ?? ''}::${action.action_key}`;
        const existing = target.get(identity);
        if (!existing || action.created_at > existing.created_at) {
          target.set(identity, action);
        }
      }

      return {
        code,
        title: titleByCode.get(code) ?? code,
        buttons: Array.from(buttonsByIdentity.values()),
        autoActions: Array.from(autoByIdentity.values()),
        history: actions,
      };
    });
  });

  /** Top-level inventory keys as canvas nodes, each with its nested paths flattened into
   * dotted signal paths — the ones matching a capability's declared signal are clickable. */
  readonly inventoryNodes = computed<InventoryNode[]>(() => {
    const yamlText = this.instance()?.latest_yaml;
    if (!yamlText) {
      return [];
    }
    let parsed: unknown;
    try {
      parsed = parseYaml(yamlText);
    } catch {
      return [];
    }
    if (parsed == null || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return [];
    }

    const signalPaths = new Set(
      (this.capabilities()?.capabilities ?? []).flatMap((c) => c.signals.map((s) => s.path)),
    );

    return Object.entries(parsed as Record<string, unknown>).map(([key, value]) => {
      const isObject = value != null && typeof value === 'object' && !Array.isArray(value);
      const flattened = isObject ? this.flattenYaml(value, key) : [{ path: key, value }];
      return {
        key,
        signals: flattened.map((s) => ({ ...s, isSignal: signalPaths.has(s.path) })),
      };
    });
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
      const [instance, actions, capabilities] = await Promise.all([
        this.lodgeService.getInstance(this.kindCode, this.instanceCode),
        this.lodgeService.getActions(this.kindCode, this.instanceCode),
        this.lodgeService.getCapabilities(this.kindCode),
      ]);
      this.instance.set(instance);
      this.actions.set(actions);
      this.capabilities.set(capabilities);
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

  async confirm(action: ActionModel, prompts: Record<string, any>) {
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
      } else if (result.execution_ref) {
        // Follow the run live instead of a fire-and-forget toast.
        this.openLog({ ...action, status: 'RUNNING', execution_ref: result.execution_ref });
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
      this.snackBar.open('Reconciliation failed — see the server log.', 'Close', { duration: 5000 });
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
    return action.status === 'SUCCEEDED' && !action.synthetic;
  }

  statusColor(status: string): 'primary' | 'accent' | 'warn' {
    if (status === 'SUCCEEDED') return 'primary';
    if (status === 'FAILED') return 'warn';
    return 'accent';
  }

  controlIcon(action: ActionModel): string {
    if (action.status === 'RUNNING') return 'autorenew';
    if (action.status === 'FAILED') return 'error';
    if (action.status === 'SUCCEEDED') return 'check_circle';
    return 'touch_app';
  }

  highlightSignal(path: string) {
    this.highlightedSignalPath.set(this.highlightedSignalPath() === path ? null : path);
  }

  formatSignalValue(value: unknown): string {
    if (value === null || value === undefined) {
      return '—';
    }
    return typeof value === 'string' ? value : JSON.stringify(value);
  }

  onCanvasPointerDown(event: PointerEvent) {
    this.panning = true;
    this.panStartX = event.clientX - this.canvasOffsetX();
    this.panStartY = event.clientY - this.canvasOffsetY();
  }

  onCanvasPointerMove(event: PointerEvent) {
    if (!this.panning) {
      return;
    }
    this.canvasOffsetX.set(event.clientX - this.panStartX);
    this.canvasOffsetY.set(event.clientY - this.panStartY);
  }

  onCanvasPointerUp() {
    this.panning = false;
  }

  onCanvasWheel(event: WheelEvent) {
    event.preventDefault();
    const next = this.canvasScale() - event.deltaY * 0.001;
    this.canvasScale.set(Math.min(2, Math.max(0.4, next)));
  }

  resetCanvasView() {
    this.canvasOffsetX.set(0);
    this.canvasOffsetY.set(0);
    this.canvasScale.set(1);
  }

  private flattenYaml(obj: unknown, prefix: string): { path: string; value: unknown }[] {
    if (obj == null || typeof obj !== 'object' || Array.isArray(obj)) {
      return [{ path: prefix, value: obj }];
    }
    const entries: { path: string; value: unknown }[] = [];
    for (const [key, val] of Object.entries(obj as Record<string, unknown>)) {
      const path = `${prefix}.${key}`;
      if (val != null && typeof val === 'object' && !Array.isArray(val)) {
        entries.push(...this.flattenYaml(val, path));
      } else {
        entries.push({ path, value: val });
      }
    }
    return entries;
  }
}
