import { Graph, layout } from '@dagrejs/dagre';

import { LowerCasePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';

import { ActionModel } from '../../../domain';
import { buildActionGraph, GraphEdge, GraphNode } from './action-graph.model';

const SIZE: Record<GraphNode['kind'], { width: number; height: number }> = {
  cause: { width: 150, height: 28 },
  action: { width: 230, height: 48 },
  ghost: { width: 180, height: 44 },
};

interface PlacedNode extends GraphNode {
  x: number;
  y: number;
  width: number;
  height: number;
}

interface PlacedEdge extends GraphEdge {
  path: string;
}

/**
 * The instance's actions as a graph, read right-to-left: the inventory changes sit on the left as
 * small static tags, what they set off hangs below, and every arrow points up at what its
 * node needs first. Layout by dagre (layered, left to right); the drawing is ours — SVG
 * arrows under HTML cards — so it follows the app theme. Drag to pan, wheel to zoom.
 */
@Component({
  selector: 'lodge-action-graph',
  standalone: true,
  templateUrl: './action-graph.component.html',
  styleUrls: ['./action-graph.component.scss'],
  imports: [MatButtonModule, MatIconModule, MatSlideToggleModule, LowerCasePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ActionGraphComponent {
  readonly actions = input<ActionModel[]>([]);
  /** Why the current user can't run an action, or null — same rule as the cards. */
  readonly lockedReason = input<(action: ActionModel) => string | null>(() => null);
  readonly busyActionId = input<string | null>(null);

  readonly openLog = output<ActionModel>();
  readonly confirm = output<ActionModel>();

  readonly includeSettled = signal(false);

  readonly offsetX = signal(0);
  readonly offsetY = signal(0);
  readonly scale = signal(1);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  /** Once the user pans or zooms, new data no longer re-fits the view. */
  private touched = false;
  private panning = false;
  private panStartX = 0;
  private panStartY = 0;

  readonly placed = computed(() => {
    const graph = buildActionGraph(this.actions(), this.includeSettled());

    const g = new Graph();
    g.setGraph({ rankdir: 'LR', nodesep: 12, ranksep: 40, marginx: 24, marginy: 24 });
    g.setDefaultEdgeLabel(() => ({}));
    for (const node of graph.nodes) {
      g.setNode(node.id, { ...SIZE[node.kind] });
    }
    for (const edge of graph.edges) {
      g.setEdge(edge.from, edge.to);
    }
    layout(g);

    const nodes: PlacedNode[] = graph.nodes.map((node) => {
      const at = g.node(node.id);
      return {
        ...node,
        x: at.x - at.width / 2,
        y: at.y - at.height / 2,
        width: at.width,
        height: at.height,
      };
    });
    const edges: PlacedEdge[] = graph.edges.map((edge) => {
      const points = g.edge({ v: edge.from, w: edge.to })?.points ?? [];
      return { ...edge, path: smoothPath(points) };
    });
    const size = g.graph();
    return { nodes, edges, width: size.width ?? 0, height: size.height ?? 0 };
  });

  constructor() {
    effect(() => {
      const { width, height } = this.placed();
      untracked(() => {
        if (!this.touched) {
          requestAnimationFrame(() => this.fit(width, height));
        }
      });
    });
  }

  /** Scales and centres the whole graph into the canvas (never enlarging past 1). */
  private fit(width: number, height: number) {
    const canvas = this.host.nativeElement.querySelector<HTMLElement>('.graph-canvas');
    if (!canvas || !width || !height) {
      return;
    }
    const scale = Math.min(1, canvas.clientWidth / width, canvas.clientHeight / height);
    this.scale.set(scale);
    this.offsetX.set((canvas.clientWidth - width * scale) / 2);
    this.offsetY.set((canvas.clientHeight - height * scale) / 2);
  }

  /** What the compact card leaves out: status, what it waits for, last run, lock. */
  tooltip(node: GraphNode): string {
    const action = node.action;
    if (!action) {
      return '';
    }
    const parts = [action.status.toLowerCase()];
    if (node.waitingFor.length) {
      parts.push('waiting for ' + node.waitingFor.join(', '));
    }
    if (node.optional) {
      parts.push(node.lastRun ? `last run ${node.lastRun.status.toLowerCase()}` : 'never run');
    }
    if (action.requires) {
      parts.push('requires ' + action.requires);
    }
    return parts.join(' · ');
  }

  /** QUEUED or FAILED: there's a button to press (maybe locked, maybe needing input). */
  isRunnable(action: ActionModel): boolean {
    return action.status === 'QUEUED' || action.status === 'FAILED';
  }

  /** Why the button is there but can't be pressed here, or null. */
  disabledReason(action: ActionModel): string | null {
    // Prompts are answered in the Capabilities and Run queue tabs, where the form lives.
    return (
      this.lockedReason()(action) ??
      (action.pending_prompts.length > 0 ? 'Needs input — answer it in the Run queue tab' : null)
    );
  }

  runLabel(action: ActionModel): string {
    if (action.status === 'FAILED') {
      return 'Retry';
    }
    return action.policy === 'MANUAL_REQUIRED' ? 'Confirm' : 'Run';
  }

  runIcon(action: ActionModel): string {
    if (this.lockedReason()(action)) {
      return 'lock';
    }
    if (action.pending_prompts.length > 0) {
      return 'edit_note';
    }
    return action.status === 'FAILED' ? 'replay' : 'play_arrow';
  }

  onPointerDown(event: PointerEvent) {
    if ((event.target as HTMLElement).closest('button')) {
      return;
    }
    this.panning = true;
    this.touched = true;
    this.panStartX = event.clientX - this.offsetX();
    this.panStartY = event.clientY - this.offsetY();
  }

  onPointerMove(event: PointerEvent) {
    if (!this.panning) {
      return;
    }
    this.offsetX.set(event.clientX - this.panStartX);
    this.offsetY.set(event.clientY - this.panStartY);
  }

  onPointerUp() {
    this.panning = false;
  }

  onWheel(event: WheelEvent) {
    event.preventDefault();
    this.touched = true;
    this.scale.set(Math.min(2, Math.max(0.1, this.scale() - event.deltaY * 0.001)));
  }

  resetView() {
    this.touched = false;
    const { width, height } = this.placed();
    this.fit(width, height);
  }

  statusIcon(status: string): string {
    switch (status) {
      case 'BLOCKED':
        return 'hourglass_empty';
      case 'QUEUED':
        return 'pending';
      case 'RUNNING':
        return 'progress_activity';
      case 'SUCCEEDED':
        return 'check_circle';
      case 'FAILED':
        return 'error';
      default:
        return 'radio_button_unchecked';
    }
  }

  triggerIcon(trigger: string | null): string {
    switch (trigger) {
      case 'ADD':
        return 'add_circle';
      case 'MODIFY':
        return 'edit';
      case 'DELETE':
        return 'remove_circle';
      default:
        return 'tune';
    }
  }
}

/** A dagre polyline as a smooth curve: straight through the ends, quadratic in between. */
function smoothPath(points: { x: number; y: number }[]): string {
  if (points.length === 0) {
    return '';
  }
  let d = `M ${points[0].x} ${points[0].y}`;
  for (let i = 1; i < points.length - 1; i++) {
    const mid = { x: (points[i].x + points[i + 1].x) / 2, y: (points[i].y + points[i + 1].y) / 2 };
    d += ` Q ${points[i].x} ${points[i].y} ${mid.x} ${mid.y}`;
  }
  const last = points[points.length - 1];
  return `${d} L ${last.x} ${last.y}`;
}
