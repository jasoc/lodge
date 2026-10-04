import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSort, MatSortModule } from '@angular/material/sort';
import { MatTableDataSource, MatTableModule } from '@angular/material/table';

import { GlobalAuditEventModel } from '../../domain';
import { autoRefresh } from '../../services/auto-refresh';
import { LodgeService } from '../../services/lodge.service';

type Payload = Record<string, unknown>;

/** One `path: from → to` line of a `registry.revision.stored` payload. */
interface ValueChange {
  path: string;
  from: unknown;
  to: unknown;
}

/** A payload field ready to render: scalars inline, objects/arrays pretty-printed. */
interface PayloadField {
  key: string;
  text: string;
  block: boolean;
}

/** An audit row with its payload parsed once, up front, instead of per change detection. */
interface AuditRow extends GlobalAuditEventModel {
  payload: Payload;
  summary: string;
  /** Signal value changes of a revision event, values already rendered as text. */
  changes: ValueChange[];
  fields: PayloadField[];
}

/** Every audit event across every kind/instance — one canonical audit UI, optionally
 * pre-filtered to a single instance via `?instance_id=`, e.g. linked from the instance
 * detail page. A row expands to the full event payload (signal values, inputs, run outcome). */
@Component({
  selector: 'lodge-audit',
  standalone: true,
  templateUrl: './audit.component.html',
  styleUrls: ['./audit.component.scss'],
  imports: [MatTableModule, MatSortModule, MatButtonModule, MatIconModule, RouterLink, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AuditComponent {
  private readonly lodgeService = inject(LodgeService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sort = viewChild(MatSort);

  readonly loading = signal(true);
  readonly instanceIdFilter = signal<string | null>(
    this.route.snapshot.queryParamMap.get('instance_id'),
  );
  readonly expandedId = signal<string | null>(null);
  readonly displayedColumns: string[] = [
    'expand',
    'kind_code',
    'instance_display_name',
    'event_type',
    'summary',
    'actor',
    'created_at',
  ];
  readonly dataSource = new MatTableDataSource<AuditRow>();

  constructor() {
    this.load();
    autoRefresh(() => this.load({ silent: true }));

    effect(() => {
      const s = this.sort();
      if (s) {
        this.dataSource.sort = s;
      }
    });
  }

  async load(opts: { silent?: boolean } = {}) {
    if (!opts.silent) {
      this.loading.set(true);
    }
    try {
      const rows = await this.lodgeService.getAllEvents({
        instanceId: this.instanceIdFilter() ?? undefined,
      });
      this.dataSource.data = rows.map(toAuditRow);
    } finally {
      this.loading.set(false);
    }
  }

  clearInstanceFilter() {
    this.instanceIdFilter.set(null);
    this.router.navigate([], { queryParams: {} });
    this.load();
  }

  toggle(row: AuditRow) {
    this.expandedId.update((id) => (id === row.id ? null : row.id));
  }
}

function toAuditRow(event: GlobalAuditEventModel): AuditRow {
  const payload = parsePayload(event.payload_json);
  const changes = readChanges(payload).map((c) => ({
    path: c.path,
    from: formatValue(c.from, Infinity),
    to: formatValue(c.to, Infinity),
  }));
  const fields = Object.entries(payload)
    .filter(([key]) => key !== 'changes')
    .map(([key, value]) => toField(key, value));
  return { ...event, payload, changes, fields, summary: summarize(event.event_type, payload) };
}

function parsePayload(json: string | null): Payload {
  if (!json) {
    return {};
  }
  try {
    const parsed = JSON.parse(json);
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : { value: parsed };
  } catch {
    return { raw: json };
  }
}

function readChanges(payload: Payload): ValueChange[] {
  return Array.isArray(payload['changes']) ? (payload['changes'] as ValueChange[]) : [];
}

function toField(key: string, value: unknown): PayloadField {
  const block = value !== null && typeof value === 'object';
  return { key, block, text: block ? JSON.stringify(value, null, 2) : formatValue(value) };
}

/** A JSON value as one line: strings bare, everything else as JSON, cut at `max` chars. */
function formatValue(value: unknown, max = 80): string {
  if (value === undefined || value === null) {
    return '∅';
  }
  if (typeof value === 'string') {
    return value;
  }
  const text = JSON.stringify(value);
  return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}

/** Reads a payload key in either casing — events written before payloads were enriched
 * used PascalCase property names (`SignalPath`), newer ones camelCase (`signalPath`). */
function field(payload: Payload, key: string): unknown {
  return payload[key] ?? payload[key[0].toUpperCase() + key.slice(1)];
}

/** One-line, human-readable gist of an event for the table; the full payload is in the
 * expanded row. */
function summarize(eventType: string, payload: Payload): string {
  if (eventType === 'registry.revision.stored') {
    const changes = readChanges(payload);
    const ref = String(field(payload, 'gitRef') ?? '').slice(0, 12);
    if (!changes.length) {
      return ref ? `revision ${ref}` : '';
    }
    const shown = changes
      .slice(0, 2)
      .map((c) => `${c.path}: ${formatValue(c.from)} → ${formatValue(c.to)}`);
    const more = changes.length > 2 ? ` (+${changes.length - 2} more)` : '';
    return `${shown.join(', ')}${more}`;
  }

  const signalPath = field(payload, 'signalPath');
  if (typeof signalPath !== 'string') {
    const code = field(payload, 'code') ?? field(payload, 'instance');
    return code ? String(code) : '';
  }

  const itemKey = field(payload, 'itemKey');
  const actionKey = field(payload, 'actionKey');
  const target = `${signalPath}${itemKey ? `[${itemKey}]` : ''}${actionKey ? ` · ${actionKey}` : ''}`;
  const desired = field(payload, 'desiredValue') ?? field(payload, 'desiredValueJson');

  switch (eventType) {
    case 'action.generated':
      return 'previousValue' in payload
        ? `${target}: ${formatValue(payload['previousValue'])} → ${formatValue(desired)}`
        : `${target}: ${formatValue(desired)}`;
    case 'action.superseded':
      return 'newDesiredValue' in payload
        ? `${target}: ${formatValue(desired)} → ${formatValue(payload['newDesiredValue'])}`
        : `${target}: ${formatValue(desired)} (no longer required)`;
    case 'action.execution.completed': {
      const state = field(payload, 'state');
      const seconds = payload['durationSeconds'];
      return `${target}: ${state ?? ''}${typeof seconds === 'number' ? ` in ${seconds}s` : ''}`;
    }
    case 'action.denied':
      return `${target}: ${payload['operation'] ?? 'denied'} needs '${field(payload, 'requires')}'`;
    default:
      return desired !== undefined ? `${target}: ${formatValue(desired)}` : target;
  }
}
