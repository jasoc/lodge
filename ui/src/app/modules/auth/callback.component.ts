import { ChangeDetectionStrategy, Component, inject, OnInit } from '@angular/core';
import { Router } from '@angular/router';

import { AuthService } from '../../services/auth.service';

/**
 * Where the server's OIDC callback redirects the browser to, with the minted token in
 * the URL fragment (`#token=...&subject_id=...`) — a fragment, not a query string, so it
 * is read here client-side and never transmitted to any server again. Renders nothing
 * visible; it's a pure landing/redirect step.
 */
@Component({
  selector: 'lodge-auth-callback',
  standalone: true,
  template: '',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AuthCallbackComponent implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  ngOnInit(): void {
    const params = new URLSearchParams(location.hash.replace(/^#/, ''));
    const token = params.get('token');
    const subjectId = params.get('subject_id');

    if (token && subjectId) {
      this.auth.setSession(token, subjectId);
      // Clears the token from the URL/history before navigating anywhere else.
      history.replaceState(null, '', location.pathname);
      this.router.navigateByUrl('/home');
    } else {
      this.router.navigateByUrl('/auth/login');
    }
  }
}
