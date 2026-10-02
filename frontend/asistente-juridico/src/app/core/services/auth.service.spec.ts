import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AuthService } from './auth.service';
import { ApiResponse, LoginRequest, LoginResponse, User } from '../models/auth.models';

describe('AuthService', () => {
  let service: AuthService;
  let httpMock: HttpTestingController;

  const mockUser: User = {
    id: 'user-123',
    email: 'abogado@test.ec',
    nombreCompleto: 'Dr. Test',
    rol: 'AbogadoSenior',
    tenantId: 'tenant-123',
    tenantSlug: 'demo-estudio',
    tenantNombre: 'Demo Estudio',
    permissions: ['Expedientes.Read', 'Expedientes.Create']
  };

  const mockLoginResponse: ApiResponse<LoginResponse> = {
    success: true,
    data: {
      accessToken: 'sample-jwt-token-123',
      expiresIn: 900,
      tokenType: 'Bearer',
      user: mockUser
    },
    message: 'Login exitoso',
    errors: [],
    timestamp: new Date().toISOString()
  };

  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();

    TestBed.configureTestingModule({
      providers: [
        AuthService,
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    service = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    localStorage.clear();
    sessionStorage.clear();
  });

  it('should be created with initial null state in memory', () => {
    expect(service).toBeTruthy();
    expect(service.accessToken()).toBeNull();
    expect(service.currentUser()).toBeNull();
    expect(service.isAuthenticated()).toBe(false);
  });

  it('should store token and user in memory signals upon successful login', () => {
    const loginReq: LoginRequest = {
      email: 'abogado@test.ec',
      password: 'Password123!*',
      tenantSlug: 'demo-estudio'
    };

    service.login(loginReq).subscribe(res => {
      expect(res.success).toBe(true);
      expect(res.data.accessToken).toBe('sample-jwt-token-123');
    });

    const req = httpMock.expectOne('http://localhost:5270/api/v1/auth/login');
    expect(req.request.method).toBe('POST');
    expect(req.request.withCredentials).toBe(true); // Valida envío de cookies
    req.flush(mockLoginResponse);

    // Verificar estado en memoria
    expect(service.accessToken()).toBe('sample-jwt-token-123');
    expect(service.currentUser()?.email).toBe('abogado@test.ec');
    expect(service.isAuthenticated()).toBe(true);
    expect(service.hasPermission('Expedientes.Create')).toBe(true);
    expect(service.hasPermission('Expedientes.Delete')).toBe(false);
    expect(service.hasRole('AbogadoSenior')).toBe(true);

    // GARANTÍA CRÍTICA DE SEGURIDAD: CERO ALMACENAMIENTO EN DISCO O STORAGE WEB
    expect(localStorage.getItem('accessToken')).toBeNull();
    expect(localStorage.getItem('refreshToken')).toBeNull();
    expect(sessionStorage.getItem('accessToken')).toBeNull();
    expect(sessionStorage.getItem('refreshToken')).toBeNull();
  });

  it('should clear in-memory state on logout', () => {
    const loginReq: LoginRequest = {
      email: 'abogado@test.ec',
      password: 'Password123!*',
      tenantSlug: 'demo-estudio'
    };

    service.login(loginReq).subscribe();
    httpMock.expectOne('http://localhost:5270/api/v1/auth/login').flush(mockLoginResponse);

    expect(service.isAuthenticated()).toBe(true);

    service.logout().subscribe();
    const logoutReq = httpMock.expectOne('http://localhost:5270/api/v1/auth/logout');
    expect(logoutReq.request.method).toBe('POST');
    logoutReq.flush({ success: true, message: 'Logout exitoso' });

    expect(service.accessToken()).toBeNull();
    expect(service.currentUser()).toBeNull();
    expect(service.isAuthenticated()).toBe(false);
  });
});
