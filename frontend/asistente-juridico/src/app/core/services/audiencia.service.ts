import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse } from '../models/auth.models';
import {
  AudienciaDto,
  CreateAudienciaDto,
  PagedResponse,
  UpdateAudienciaDto
} from '../models/fase4.models';

@Injectable({
  providedIn: 'root'
})
export class AudienciaService {
  private readonly http = inject(HttpClient);
  private readonly apiUrl = 'http://localhost:5270/api/v1/audiencias';

  getAudiencias(
    expedienteId?: string,
    estado?: string,
    pageNumber = 1,
    pageSize = 10
  ): Observable<ApiResponse<PagedResponse<AudienciaDto>>> {
    let params = new HttpParams()
      .set('pageNumber', pageNumber.toString())
      .set('pageSize', pageSize.toString());

    if (expedienteId) {
      params = params.set('expedienteId', expedienteId);
    }
    if (estado) {
      params = params.set('estado', estado);
    }

    return this.http.get<ApiResponse<PagedResponse<AudienciaDto>>>(this.apiUrl, { params });
  }

  getAudienciaById(id: string): Observable<ApiResponse<AudienciaDto>> {
    return this.http.get<ApiResponse<AudienciaDto>>(`${this.apiUrl}/${id}`);
  }

  createAudiencia(dto: CreateAudienciaDto): Observable<ApiResponse<AudienciaDto>> {
    return this.http.post<ApiResponse<AudienciaDto>>(this.apiUrl, dto);
  }

  updateAudiencia(id: string, dto: UpdateAudienciaDto): Observable<ApiResponse<AudienciaDto>> {
    return this.http.put<ApiResponse<AudienciaDto>>(`${this.apiUrl}/${id}`, dto);
  }

  deleteAudiencia(id: string): Observable<ApiResponse<boolean>> {
    return this.http.delete<ApiResponse<boolean>>(`${this.apiUrl}/${id}`);
  }
}
