import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse } from '../models/auth.models';
import {
  CambiarEstadoExpedienteDto,
  CreateExpedienteDto,
  ExpedienteDetailDto,
  ExpedienteDto,
  ExpedienteProcesoJudicialDto,
  PagedResponse,
  UpdateExpedienteDto,
  VincularProcesoDto
} from '../models/fase4.models';

@Injectable({
  providedIn: 'root'
})
export class ExpedienteService {
  private readonly http = inject(HttpClient);
  private readonly apiUrl = 'http://localhost:5270/api/v1/expedientes';

  getExpedientes(
    pageNumber = 1,
    pageSize = 10,
    search?: string,
    estado?: string,
    clienteId?: string
  ): Observable<ApiResponse<PagedResponse<ExpedienteDto>>> {
    let params = new HttpParams()
      .set('pageNumber', pageNumber.toString())
      .set('pageSize', pageSize.toString());

    if (search) {
      params = params.set('search', search);
    }
    if (estado) {
      params = params.set('estado', estado);
    }
    if (clienteId) {
      params = params.set('clienteId', clienteId);
    }

    return this.http.get<ApiResponse<PagedResponse<ExpedienteDto>>>(this.apiUrl, { params });
  }

  getExpedienteById(id: string): Observable<ApiResponse<ExpedienteDetailDto>> {
    return this.http.get<ApiResponse<ExpedienteDetailDto>>(`${this.apiUrl}/${id}`);
  }

  createExpediente(dto: CreateExpedienteDto): Observable<ApiResponse<ExpedienteDto>> {
    return this.http.post<ApiResponse<ExpedienteDto>>(this.apiUrl, dto);
  }

  updateExpediente(id: string, dto: UpdateExpedienteDto): Observable<ApiResponse<ExpedienteDto>> {
    return this.http.put<ApiResponse<ExpedienteDto>>(`${this.apiUrl}/${id}`, dto);
  }

  cambiarEstado(id: string, dto: CambiarEstadoExpedienteDto): Observable<ApiResponse<ExpedienteDto>> {
    return this.http.patch<ApiResponse<ExpedienteDto>>(`${this.apiUrl}/${id}/estado`, dto);
  }

  vincularProceso(id: string, dto: VincularProcesoDto): Observable<ApiResponse<ExpedienteProcesoJudicialDto>> {
    return this.http.post<ApiResponse<ExpedienteProcesoJudicialDto>>(`${this.apiUrl}/${id}/procesos-judiciales`, dto);
  }

  desvincularProceso(id: string, procesoJudicialId: string): Observable<ApiResponse<boolean>> {
    return this.http.delete<ApiResponse<boolean>>(`${this.apiUrl}/${id}/procesos-judiciales/${procesoJudicialId}`);
  }
}
