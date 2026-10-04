import { inject, Injectable } from '@angular/core';

import {
  ActionExecutionResultModel,
  ActionLogModel,
  ActionModel,
  AuditEventModel,
  CapabilityCatalogModel,
  CapabilityViewModel,
  CycleSummaryModel,
  SyncCyclesModel,
  GlobalActionModel,
  GlobalAuditEventModel,
  KindModel,
  InstanceDetailModel,
  InstanceModel,
} from '../domain';

/** All-optional, AND-combined filters for the cross-instance actions/events read surface. */
export interface GlobalActionsFilter {
  status?: string;
  policy?: string;
  kindCode?: string;
  instanceId?: string;
}

export interface GlobalEventsFilter {
  kindCode?: string;
  instanceId?: string;
}
import { AuthService } from './auth.service';
import { BackendService } from './backend.service';

/** The UI's read/write surface over Lodge's reconciliation API — the same endpoints the
 * CLI talks to, so the two can never disagree about what's true. */
@Injectable({
  providedIn: 'root',
})
export class LodgeService extends BackendService {
  private readonly authService = inject(AuthService);
  async getKinds(): Promise<KindModel[]> {
    const res = await this.get<KindModel[]>('/kinds');
    return res.body!;
  }

  async getCapabilities(kindCode: string): Promise<CapabilityCatalogModel> {
    const res = await this.get<CapabilityCatalogModel>(`/kinds/${kindCode}/capabilities`);
    return res.body!;
  }

  async getInstances(kindCode: string): Promise<InstanceModel[]> {
    const res = await this.get<InstanceModel[]>(`/kinds/${kindCode}/instances`);
    return res.body!;
  }

  async getInstance(kindCode: string, instanceCode: string): Promise<InstanceDetailModel> {
    const res = await this.get<InstanceDetailModel>(`/kinds/${kindCode}/instances/${instanceCode}`);
    return res.body!;
  }

  async getActions(kindCode: string, instanceCode: string): Promise<ActionModel[]> {
    const res = await this.get<ActionModel[]>(
      `/kinds/${kindCode}/instances/${instanceCode}/actions`,
    );
    return res.body!;
  }

  async getEvents(kindCode: string, instanceCode: string): Promise<AuditEventModel[]> {
    const res = await this.get<AuditEventModel[]>(
      `/kinds/${kindCode}/instances/${instanceCode}/events`,
    );
    return res.body!;
  }

  async getActionLog(
    kindCode: string,
    instanceCode: string,
    actionId: string,
    offset: number,
  ): Promise<ActionLogModel> {
    const res = await this.get<ActionLogModel>(
      `/kinds/${kindCode}/instances/${instanceCode}/actions/${actionId}/log?offset=${offset}`,
    );
    return res.body!;
  }

  async confirmAction(
    kindCode: string,
    instanceCode: string,
    actionId: string,
    prompts?: Record<string, string>,
  ): Promise<ActionExecutionResultModel> {
    const res = await this.post<ActionExecutionResultModel>(
      `/kinds/${kindCode}/instances/${instanceCode}/actions/${actionId}/confirm`,
      { actor: this.authService.subjectId(), prompts },
    );
    return res.body!;
  }

  async invalidateAction(
    kindCode: string,
    instanceCode: string,
    actionId: string,
  ): Promise<ActionExecutionResultModel> {
    const res = await this.post<ActionExecutionResultModel>(
      `/kinds/${kindCode}/instances/${instanceCode}/actions/${actionId}/invalidate`,
      { actor: this.authService.subjectId() },
    );
    return res.body!;
  }

  async getActionStatus(
    kindCode: string,
    instanceCode: string,
    actionId: string,
  ): Promise<ActionExecutionResultModel> {
    const res = await this.get<ActionExecutionResultModel>(
      `/kinds/${kindCode}/instances/${instanceCode}/actions/${actionId}/status`,
    );
    return res.body!;
  }

  /** View capabilities (no rules) rendered against the instance's latest inventory. */
  async getViews(kindCode: string, instanceCode: string): Promise<CapabilityViewModel[]> {
    const res = await this.get<CapabilityViewModel[]>(
      `/kinds/${kindCode}/instances/${instanceCode}/views`,
    );
    return res.body!;
  }

  /** The latest reconciliation cycles, newest first, with their validation errors. */
  async getCycles(limit = 20): Promise<SyncCyclesModel> {
    const res = await this.get<SyncCyclesModel>('/reconcile/cycles', { limit });
    return res.body!;
  }

  async reconcile(): Promise<CycleSummaryModel> {
    const res = await this.post<CycleSummaryModel>('/reconcile', {});
    return res.body!;
  }

  /** Every action across every kind/instance — backs the Drift and Actions pages. */
  async getAllActions(filter?: GlobalActionsFilter): Promise<GlobalActionModel[]> {
    const params: Record<string, string> = {};
    if (filter?.status) params['status'] = filter.status;
    if (filter?.policy) params['policy'] = filter.policy;
    if (filter?.kindCode) params['kind_code'] = filter.kindCode;
    if (filter?.instanceId) params['instance_id'] = filter.instanceId;
    const res = await this.get<GlobalActionModel[]>('/actions', params);
    return res.body!;
  }

  /** Every audit event across every kind/instance — backs the Audit Log page. */
  async getAllEvents(filter?: GlobalEventsFilter): Promise<GlobalAuditEventModel[]> {
    const params: Record<string, string> = {};
    if (filter?.kindCode) params['kind_code'] = filter.kindCode;
    if (filter?.instanceId) params['instance_id'] = filter.instanceId;
    const res = await this.get<GlobalAuditEventModel[]>('/events', params);
    return res.body!;
  }
}
