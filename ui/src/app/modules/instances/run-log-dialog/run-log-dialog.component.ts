import {
  AfterViewChecked,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogClose,
  MatDialogContent,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';

import { ActionModel } from '../../../domain';
import { LodgeService } from '../../../services/lodge.service';

export interface RunLogDialogData {
  kindCode: string;
  instanceCode: string;
  action: ActionModel;
}

const POLL_MS = 1500;

/**
 * Live view of an action's latest run: the container/script's combined stdout/stderr
 * (image build output included for docker playbooks), tailed by byte offset while the run
 * is in flight, plus the executor's own status message — which is where a refusal or a
 * failure reason shows up even when the process itself printed nothing.
 */
@Component({
  selector: 'lodge-run-log-dialog',
  standalone: true,
  templateUrl: './run-log-dialog.component.html',
  styleUrls: ['./run-log-dialog.component.scss'],
  imports: [
    MatButtonModule,
    MatChipsModule,
    MatIconModule,
    MatProgressBarModule,
    MatDialogTitle,
    MatDialogContent,
    MatDialogActions,
    MatDialogClose,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunLogDialogComponent implements AfterViewChecked {
  readonly data = inject<RunLogDialogData>(MAT_DIALOG_DATA);
  private readonly lodgeService = inject(LodgeService);

  readonly text = signal('');
  readonly running = signal(true);
  readonly available = signal(true);
  readonly message = signal<string | null>(null);
  readonly status = signal(this.data.action.status as string);
  readonly error = signal<string | null>(null);

  private readonly logView = viewChild<ElementRef<HTMLElement>>('logView');
  private offset = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private stickToBottom = true;
  private destroyed = false;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      if (this.timer) {
        clearTimeout(this.timer);
      }
    });
    void this.poll();
  }

  ngAfterViewChecked(): void {
    const el = this.logView()?.nativeElement;
    if (el && this.stickToBottom) {
      el.scrollTop = el.scrollHeight;
    }
  }

  /** Follow new output only while the user is at the bottom — scrolling up pauses it. */
  onScroll(): void {
    const el = this.logView()?.nativeElement;
    if (el) {
      this.stickToBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 24;
    }
  }

  statusColor(): 'primary' | 'accent' | 'warn' {
    const status = this.status();
    if (status === 'SUCCEEDED') return 'primary';
    if (status === 'FAILED') return 'warn';
    return 'accent';
  }

  private async poll(): Promise<void> {
    try {
      const chunk = await this.lodgeService.getActionLog(
        this.data.kindCode,
        this.data.instanceCode,
        this.data.action.id,
        this.offset,
      );
      if (chunk.text) {
        this.text.update((t) => t + chunk.text);
      }
      this.offset = chunk.next_offset;
      this.available.set(chunk.available);
      this.running.set(chunk.running);
      this.message.set(chunk.message);
      this.status.set(chunk.running ? 'RUNNING' : chunk.action_status);
      this.error.set(null);

      // Keep tailing while the run is live; once it ends, one more read catches whatever
      // was flushed between the last slice and the exit.
      const more = chunk.running || chunk.text.length > 0;
      if (more && !this.destroyed) {
        this.timer = setTimeout(() => void this.poll(), chunk.running ? POLL_MS : 0);
      }
    } catch {
      this.error.set('Could not load the run log — retrying…');
      if (!this.destroyed) {
        this.timer = setTimeout(() => void this.poll(), POLL_MS * 2);
      }
    }
  }
}
