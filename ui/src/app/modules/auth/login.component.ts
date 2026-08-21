import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { AuthService } from '../../services/auth.service';

/**
 * Only reachable when there's no session yet in the Oidc profile — `PermissionsService`'s
 * route guard sends the browser here instead of activating the shell. There's nothing to
 * do in the NoAuth profile (the guard mints silently), so this page never renders there.
 * "Sign in" is a full page navigation, not an XHR call: the server itself drives the OIDC
 * redirect to the IdP (see docs/AGENTS.md invariant 10) — this SPA never talks to it directly.
 */
@Component({
  selector: 'lodge-login',
  standalone: true,
  templateUrl: './login.component.html',
  styleUrls: ['./login.component.scss'],
  imports: [MatButtonModule, MatCardModule, MatProgressSpinnerModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LoginComponent implements OnInit {
  private readonly auth = inject(AuthService);

  readonly checking = signal(true);

  async ngOnInit(): Promise<void> {
    // A stray visit here after a session was already established elsewhere (another tab,
    // a back-navigation) should just proceed — no need to force a second SSO round trip.
    if (this.auth.userLogged()) {
      location.href = '/';
      return;
    }
    this.checking.set(false);
  }

  signIn(): void {
    location.href = this.auth.ssoLoginUrl();
  }
}
