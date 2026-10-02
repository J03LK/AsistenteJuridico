import { Injectable, inject, signal, computed } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, tap, catchError, throwError } from 'rxjs';
import {
  ApiResponse,
  LoginRequest,
  LoginResponse,
  RefreshTokenResponse,
  User,
  ChangePasswordRequest,
  ForgotPasswordRequest,
  ResetPasswordRequest
} from '../models/auth.models';

@Injectable({
  providedIn: 'root'
})
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly apiUrl = 'http://localhost:5270/api/v1/auth';

  // ──────────────────────────────────────────────────────────
  // ESTADO DE SESIÓN ESTRICTAMENTE EN MEMORIA (SIGNALS)
  // Ningún token se almacena en localStorage ni sessionStorage.
  // ──────────────────────────────────────────────────────────
  private readonly _accessToken = signal<string | null>(null);
  private readonly _currentUser = signal<User | null>(null);
  private refreshTimer: ReturnType<typeof setTimeout> | null = null;

  readonly accessToken = this._accessToken.asReadonly();
  readonly currentUser = this._currentUser.asReadonly();
  readonly isAuthenticated = computed(() => !!this._accessToken());
  readonly currentTenantId = computed(() => this._currentUser()?.tenantId ?? null);
  readonly currentTenantSlug = computed(() => this._currentUser()?.tenantSlug ?? null);

  /**
   * Retorna el Access Token actual en memoria para interceptores.
   */
  getAccessToken(): string | null {
    return this._accessToken();
  }

  /**
   * Inicia sesión en el backend.
   * El backend entrega el Access Token en el body y el Refresh Token en Cookie HttpOnly.
   */
  login(request: LoginRequest): Observable<ApiResponse<LoginResponse>> {
    return this.http.post<ApiResponse<LoginResponse>>(`${this.apiUrl}/login`, request, {
      withCredentials: true // Obligatorio para recibir y enviar cookies HttpOnly y SameSite
    }).pipe(
      tap(res => {
        if (res.success && res.data) {
          this.setSession(res.data.accessToken, res.data.expiresIn, res.data.user);
        }
      })
    );
  }

  /**
   * Rota el Refresh Token y obtiene un nuevo Access Token.
   * La cookie HttpOnly se adjunta automáticamente por el navegador.
   * No requiere Access Token previo.
   */
  refreshToken(): Observable<ApiResponse<RefreshTokenResponse>> {
    return this.http.post<ApiResponse<RefreshTokenResponse>>(`${this.apiUrl}/refresh-token`, {}, {
      withCredentials: true
    }).pipe(
      tap(res => {
        if (res.success && res.data) {
          this._accessToken.set(res.data.accessToken);
          this.scheduleSilentRefresh(res.data.expiresIn);
        }
      }),
      catchError(err => {
        this.clearSession();
        return throwError(() => err);
      })
    );
  }

  /**
   * Cierra sesión revocando el token en servidor y limpiando la memoria.
   */
  logout(): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.apiUrl}/logout`, {}, {
      withCredentials: true
    }).pipe(
      tap(() => this.clearSession()),
      catchError(err => {
        this.clearSession();
        return throwError(() => err);
      })
    );
  }

  /**
   * Revoca todas las sesiones activas del usuario.
   */
  revokeAllSessions(): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.apiUrl}/revoke-all`, {}, {
      withCredentials: true
    }).pipe(
      tap(() => this.clearSession())
    );
  }

  /**
   * Solicita enlace de restablecimiento de contraseña.
   */
  forgotPassword(request: ForgotPasswordRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.apiUrl}/forgot-password`, request);
  }

  /**
   * Restablece contraseña mediante token criptográfico.
   */
  resetPassword(request: ResetPasswordRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.apiUrl}/reset-password`, request);
  }

  /**
   * Cambia la contraseña del usuario autenticado.
   */
  changePassword(request: ChangePasswordRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.apiUrl}/change-password`, request, {
      withCredentials: true
    });
  }

  /**
   * Obtiene o renueva los datos del usuario en sesión.
   */
  loadCurrentUser(): Observable<ApiResponse<User>> {
    return this.http.get<ApiResponse<User>>(`${this.apiUrl}/me`, {
      withCredentials: true
    }).pipe(
      tap(res => {
        if (res.success && res.data) {
          this._currentUser.set(res.data);
        }
      })
    );
  }

  /**
   * Comprueba si el usuario tiene un permiso específico (PBAC).
   */
  hasPermission(permission: string): boolean {
    const user = this._currentUser();
    return user ? user.permissions.includes(permission) : false;
  }

  /**
   * Comprueba si el usuario tiene un rol específico (RBAC).
   */
  hasRole(role: string): boolean {
    const user = this._currentUser();
    return user ? user.rol === role : false;
  }

  // ──────────────────────────────────────────────────────────
  // GESTIÓN DE SESIÓN EN MEMORIA Y RENOVACIÓN SILENCIOSA
  // ──────────────────────────────────────────────────────────

  private setSession(accessToken: string, expiresInSeconds: number, user: User): void {
    this._accessToken.set(accessToken);
    this._currentUser.set(user);
    this.scheduleSilentRefresh(expiresInSeconds);
  }

  private clearSession(): void {
    this._accessToken.set(null);
    this._currentUser.set(null);
    if (this.refreshTimer) {
      clearTimeout(this.refreshTimer);
      this.refreshTimer = null;
    }
  }

  private scheduleSilentRefresh(expiresInSeconds: number): void {
    if (this.refreshTimer) {
      clearTimeout(this.refreshTimer);
    }

    // Renovar 2 minutos antes de la expiración (o a los 30 segundos si dura menos)
    const refreshDelayMs = Math.max((expiresInSeconds - 120) * 1000, 30000);

    this.refreshTimer = setTimeout(() => {
      if (this._accessToken()) {
        this.refreshToken().subscribe({
          error: () => this.clearSession()
        });
      }
    }, refreshDelayMs);
  }
}
