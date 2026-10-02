import { HttpInterceptorFn, HttpRequest, HttpHandlerFn, HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';

/**
 * Interceptor HTTP global de manejo de errores.
 * Captura errores 401/403 y redirige al login.
 * Muestra mensajes de error apropiados para otros códigos.
 */
export const errorInterceptor: HttpInterceptorFn = (
  req: HttpRequest<unknown>,
  next: HttpHandlerFn
) => {
  const router = inject(Router);

  return next(req).pipe(
    catchError((error: HttpErrorResponse) => {
      if (error.status === 401) {
        // Token expirado o no autenticado → redirigir al login
        router.navigate(['/auth/login']);
      }

      if (error.status === 403) {
        // Sin permisos → redirigir a página de acceso denegado
        router.navigate(['/forbidden']);
      }

      return throwError(() => error);
    })
  );
};
