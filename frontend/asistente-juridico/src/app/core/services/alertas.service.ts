import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse } from '../models/auth.models';
import {
  AlertaProcesalDto,
  AlertasFilterRequest,
  ConteoNoLeidasDto,
  MarcarTodasLeidasRequest,
  MarcarTodasLeidasResponseDto
} from '../models/fase5.models';

@Injectable({
  providedIn: 'root'
})
export class AlertasService {
  private readonly http = inject(HttpClient);
  private readonly apiUrl = 'http://localhost:5270/api/v1/alertas';

  getAlertas(filter?: AlertasFilterRequest): Observable<ApiResponse<AlertaProcesalDto[]>> {
    let params = new HttpParams();
    if (filter?.soloNoLeidas !== undefined && filter?.soloNoLeidas !== null) {
      params = params.set('soloNoLeidas', filter.soloNoLeidas.toString());
    }
    if (filter?.incluirResueltas !== undefined && filter?.incluirResueltas !== null) {
      params = params.set('incluirResueltas', filter.incluirResueltas.toString());
    }
    if (filter?.pageNumber) {
      params = params.set('pageNumber', filter.pageNumber.toString());
    }
    if (filter?.pageSize) {
      params = params.set('pageSize', filter.pageSize.toString());
    }

    return this.http.get<ApiResponse<AlertaProcesalDto[]>>(this.apiUrl, { params });
  }

  getConteoNoLeidas(): Observable<ApiResponse<ConteoNoLeidasDto>> {
    return this.http.get<ApiResponse<ConteoNoLeidasDto>>(`${this.apiUrl}/conteo-no-leidas`);
  }

  marcarLeida(id: string, version?: number): Observable<ApiResponse<AlertaProcesalDto>> {
    const body = version !== undefined ? { version } : {};
    return this.http.put<ApiResponse<AlertaProcesalDto>>(`${this.apiUrl}/${id}/marcar-leida`, body);
  }

  descartar(id: string, motivo?: string, version?: number): Observable<ApiResponse<AlertaProcesalDto>> {
    const body = { motivo, version };
    return this.http.put<ApiResponse<AlertaProcesalDto>>(`${this.apiUrl}/${id}/descartar`, body);
  }

  marcarTodasLeidas(incluirInstitucionales = false): Observable<ApiResponse<MarcarTodasLeidasResponseDto>> {
    const body: MarcarTodasLeidasRequest = { incluirInstitucionales };
    return this.http.put<ApiResponse<MarcarTodasLeidasResponseDto>>(`${this.apiUrl}/marcar-todas-leidas`, body);
  }
}
