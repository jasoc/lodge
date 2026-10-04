import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatTableModule } from '@angular/material/table';
import { RouterModule } from '@angular/router';

import { AuthConfigModel, GroupModel, UserModel } from '../../domain';
import { AuthService } from '../../services/auth.service';

/**
 * Users and groups, read-only: mirrored from the identity provider at each SSO sign-in
 * (group names 1:1). What an action's `requires: <group>` is checked against.
 */
@Component({
  selector: 'lodge-users',
  standalone: true,
  templateUrl: './users.component.html',
  styleUrls: ['./users.component.scss'],
  imports: [MatChipsModule, MatIconModule, MatTableModule, RouterModule, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UsersComponent {
  private readonly authService = inject(AuthService);

  readonly users = signal<UserModel[]>([]);
  readonly groups = signal<GroupModel[]>([]);
  readonly config = signal<AuthConfigModel | null>(null);
  readonly loading = signal(true);

  readonly userColumns = ['name', 'groups', 'last_login'];

  constructor() {
    this.load();
  }

  private async load() {
    try {
      const [users, groups, config] = await Promise.all([
        this.authService.getUsers(),
        this.authService.getGroups(),
        this.authService.getConfig(),
      ]);
      this.users.set(users);
      this.groups.set(groups);
      this.config.set(config);
    } finally {
      this.loading.set(false);
    }
  }
}
