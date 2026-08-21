import { ActivatedRoute } from '@angular/router';

import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';

import { DynamicFormComponent } from '../../components/dynamic-form/dynamic-form.component';
import { DynamicFormRoot } from '../../components/dynamic-form/types/dynamic-form';
import { TextboxElement } from '../../components/dynamic-form/types/dynamic-form-element-textbox';
import { M3TabComponent } from '../../components/m3-tabs/m3-tab/m3-tab.component';
import { M3TabsComponent } from '../../components/m3-tabs/m3-tabs.component';
import { ActionModel, AuditEventModel, InstanceDetailModel } from '../../domain';
import { LodgeService } from '../../services/lodge.service';

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
    M3TabsComponent,
    M3TabComponent,
    DynamicFormComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class InstanceDetailComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly lodgeService = inject(LodgeService);
  private readonly snackBar = inject(MatSnackBar);

  // 'kind' belongs to the parent route (see app.routes.ts — nested one level per URL
  // segment, on purpose, so the breadcrumb trail shows each segment separately).
  readonly kindCode = this.route.snapshot.parent!.paramMap.get('kind')!;
  readonly instanceCode = this.route.snapshot.paramMap.get('instance')!;

  readonly loading = signal(true);
  readonly instance = signal<InstanceDetailModel | null>(null);
  readonly actions = signal<ActionModel[]>([]);
  readonly events = signal<AuditEventModel[]>([]);

  /** Action id currently showing its pending-prompts form, if any. */
  readonly promptingActionId = signal<string | null>(null);
  readonly promptValues = signal<Record<string, any>>({});
  readonly busyActionId = signal<string | null>(null);

  constructor() {
    this.load();
  }

  async load() {
    this.loading.set(true);
    try {
      const [instance, actions, events] = await Promise.all([
        this.lodgeService.getInstance(this.kindCode, this.instanceCode),
        this.lodgeService.getActions(this.kindCode, this.instanceCode),
        this.lodgeService.getEvents(this.kindCode, this.instanceCode),
      ]);
      this.instance.set(instance);
      this.actions.set(actions);
      this.events.set(events);
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
      } else {
        this.snackBar.open(result.message ?? result.status, 'Close', { duration: 3000 });
      }
      await this.load();
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
      await this.load();
    } finally {
      this.busyActionId.set(null);
    }
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
}
