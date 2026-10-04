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

/** Mirrors `Lodge.Server.Contracts.MeDto` — who the current token belongs to. */
export interface MeModel {
  id: string;
  display_name: string;
  groups: string[];
  /** The no-auth local admin, or a member of the OIDC AdminGroup: may run every action. */
  is_admin: boolean;
  is_service_token: boolean;
}

export interface UserModel {
  id: string;
  display_name: string;
  source: 'local' | 'oidc' | string;
  groups: string[];
  created_at: string;
  last_login_at: string | null;
}

export interface GroupRequirementModel {
  kind_code: string;
  capability_code: string;
  action_key: string;
  label: string;
}

export interface GroupModel {
  name: string;
  members: string[];
  required_by: GroupRequirementModel[];
}
