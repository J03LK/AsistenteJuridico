export interface User {
  id: string;
  email: string;
  nombreCompleto: string;
  rol: string;
  tenantId: string;
  tenantSlug: string;
  tenantNombre: string;
  permissions: string[];
}

export interface LoginRequest {
  email: string;
  password: string;
  tenantSlug: string;
}

export interface LoginResponse {
  accessToken: string;
  expiresIn: number;
  tokenType: string;
  user: User;
}

export interface RefreshTokenResponse {
  accessToken: string;
  expiresIn: number;
  tokenType: string;
}

export interface ForgotPasswordRequest {
  email: string;
  tenantSlug: string;
}

export interface ResetPasswordRequest {
  email: string;
  tenantSlug: string;
  token: string;
  newPassword: string;
  confirmPassword: string;
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
  confirmPassword: string;
}

export interface ApiResponse<T> {
  success: boolean;
  data: T;
  message: string;
  errors: string[];
  timestamp: string;
}
