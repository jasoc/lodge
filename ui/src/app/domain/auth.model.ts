/** Mirrors `Lodge.Server.Contracts.LoginResponseDto`. */
export interface LoginResponseModel {
  token: string;
  subject_id: string;
  expires_at: string | null;
}

/** Mirrors `Lodge.Server.Contracts.AuthConfigDto` — tells the SPA which login flow to
 * present before it has a token to authenticate any other request with. */
export interface AuthConfigModel {
  mode: 'NoAuth' | 'Oidc' | string;
}
