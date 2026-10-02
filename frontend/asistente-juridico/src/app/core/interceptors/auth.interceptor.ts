import { HttpInterceptorFn, HttpRequest, HttpHandlerFn, HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';
import { AuthService } from '../services/auth.service';

/**
 * Interceptor de autenticación JWT.
 * Adjunta el Bearer Token desde la memoria y gestiona la auto-renovación ante respuestas 401.
 */
export const authInterceptor: HttpInterceptorFn = (
  req: HttpRequest<unknown>,
  next: HttpHandlerFn
) => {
  const authService = inject(AuthService);
  const token = authService.getAccessToken();

  let authReq = req;

  // Si la petición se dirige a nuestra API y tenemos token, adjuntarlo
  if (token && req.url.includes('/api/')) {
    authReq = req.clone({
      setHeaders: {
        Authorization: `Bearer ${token}`
      },
      withCredentials: true
    });
  } else if (req.url.includes('/api/v1/auth')) {
    // Endpoints de auth siempre transmiten cookies (refreshToken y CSRF)
    authReq = req.clone({
      withCredentials: true
    });
  }

  return next(authReq).pipe(
    catchError((error: HttpErrorResponse) => {
      // Si recibimos 401 y no es ya una petición de login o refresh, intentar renovar
      if (error.status === 401 && !req.url.includes('/auth/login') && !req.url.includes('/auth/refresh-token')) {
        return authService.refreshToken().pipe(
          switchMap(res => {
            const newToken = res.data?.accessToken;
            if (newToken) {
              const retryReq = req.clone({
                setHeaders: {
                  Authorization: `Bearer ${newToken}`
                },
                withCredentials: true
              });
              return next(retryReq);
            }
            return throwError(() => error);
          }),
          catchError(refreshError => {
            return throwError(() => refreshError);
          })
        );
      }

      return throwError(() => error);
    })
  );
};
