import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse } from '../models/auth.models';
import { DashboardResumenDto, MetricasEficienciaDto } from '../models/fase5.models';

@Injectable({
  providedIn: 'root'
})
export class DashboardService {
  private readonly http = inject(HttpClient);
  private readonly apiUrl = 'http://localhost:5270/api/v1/dashboard';

  getResumen(): Observable<ApiResponse<DashboardResumenDto>> {
    return this.http.get<ApiResponse<DashboardResumenDto>>(`${this.apiUrl}/resumen`);
  }

  getEficiencia(desdeUtc?: string, hastaUtc?: string): Observable<ApiResponse<MetricasEficienciaDto>> {
    let params = new HttpParams();
    if (desdeUtc) params = params.set('desdeUtc', desdeUtc);
    if (hastaUtc) params = params.set('hastaUtc', hastaUtc);

    return this.http.get<ApiResponse<MetricasEficienciaDto>>(`${this.apiUrl}/eficiencia`, { params });
  }
}
