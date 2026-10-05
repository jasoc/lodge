import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';

import { DynamicFormComponent } from '../../../components/dynamic-form/dynamic-form.component';
import { DynamicFormRoot } from '../../../components/dynamic-form/types/dynamic-form';
import { TextboxElement } from '../../../components/dynamic-form/types/dynamic-form-element-textbox';
import { ActionModel } from '../../../domain';
import { buildRunQueue, QueueEntry } from './run-queue.model';

export interface RunRequest {
  action: ActionModel;
  prompts: Record<string, any>;
}

/**
 * The instance's pending actions as one list in dependency order, for an operator to work
 * through top-down: press Run on the first entry, it unblocks the next ones, repeat. What's
 * in flight sits above the list, optional checks below it.
 */
@Component({
  selector: 'lodge-run-queue',
  standalone: true,
  templateUrl: './run-queue.component.html',
  styleUrls: ['./run-queue.component.scss'],
  imports: [
    MatButtonModule,
    MatExpansionModule,
    MatIconModule,
    DynamicFormComponent,
    NgTemplateOutlet,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunQueueComponent {
  readonly actions = input<ActionModel[]>([]);
  /** Why the current user can't run an action, or null — same rule as the cards. */
  readonly lockedReason = input<(action: ActionModel) => string | null>(() => null);
  readonly capabilityTitle = input<(code: string) => string>((code) => code);
  readonly busyActionId = input<string | null>(null);

  readonly run = output<RunRequest>();
  readonly openLog = output<ActionModel>();

  /** Action id currently showing its prompts form, if any. */
  readonly promptingActionId = signal<string | null>(null);
  readonly promptValues = signal<Record<string, any>>({});

  readonly runQueue = computed(() => buildRunQueue(this.actions()));

  /** The first entry the current user can press: the one the big button goes on. */
  readonly nextId = computed(
    () =>
      this.runQueue().queue.find((e) => this.isPressable(e) && !this.lockedReason()(e.action))
        ?.action.id ?? null,
  );

  isPressable(entry: QueueEntry): boolean {
    return entry.state === 'ready' || entry.state === 'failed' || entry.state === 'input';
  }

  press(entry: QueueEntry) {
    if (entry.action.pending_prompts.length > 0) {
      this.promptValues.set({});
      this.promptingActionId.set(
        this.promptingActionId() === entry.action.id ? null : entry.action.id,
      );
      return;
    }
    this.run.emit({ action: entry.action, prompts: {} });
  }

  submitPrompts(action: ActionModel) {
    this.promptingActionId.set(null);
    this.run.emit({ action, prompts: this.promptValues() });
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

  runLabel(entry: QueueEntry): string {
    switch (entry.state) {
      case 'failed':
        return 'Retry';
      case 'input':
        return 'Answer & run';
      default:
        return entry.action.policy === 'MANUAL_REQUIRED' ? 'Confirm' : 'Run';
    }
  }

  runIcon(entry: QueueEntry): string {
    if (this.lockedReason()(entry.action)) {
      return 'lock';
    }
    switch (entry.state) {
      case 'failed':
        return 'replay';
      case 'input':
        return 'edit_note';
      default:
        return 'play_arrow';
    }
  }

  stateIcon(entry: QueueEntry): string {
    switch (entry.state) {
      case 'running':
        return 'progress_activity';
      case 'starting':
        return 'schedule';
      case 'failed':
        return 'error';
      case 'blocked':
        return 'hourglass_empty';
      default:
        return entry.action.policy === 'OPTIONAL' ? 'science' : 'pending';
    }
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
}
