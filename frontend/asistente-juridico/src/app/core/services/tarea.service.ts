import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse } from '../models/auth.models';
import {
  CambiarEstadoTareaDto,
  CreateTareaDto,
  PagedResponse,
  TareaDto,
  UpdateTareaDto
} from '../models/fase4.models';

@Injectable({
  providedIn: 'root'
})
export class TareaService {
  private readonly http = inject(HttpClient);
  private readonly apiUrl = 'http://localhost:5270/api/v1/tareas';

  getTareas(
    expedienteId?: string,
    estado?: string,
    pageNumber = 1,
    pageSize = 10
  ): Observable<ApiResponse<PagedResponse<TareaDto>>> {
    let params = new HttpParams()
      .set('pageNumber', pageNumber.toString())
      .set('pageSize', pageSize.toString());

    if (expedienteId) {
      params = params.set('expedienteId', expedienteId);
    }
    if (estado) {
      params = params.set('estado', estado);
    }

    return this.http.get<ApiResponse<PagedResponse<TareaDto>>>(this.apiUrl, { params });
  }

  getTareaById(id: string): Observable<ApiResponse<TareaDto>> {
    return this.http.get<ApiResponse<TareaDto>>(`${this.apiUrl}/${id}`);
  }

  createTarea(dto: CreateTareaDto): Observable<ApiResponse<TareaDto>> {
    return this.http.post<ApiResponse<TareaDto>>(this.apiUrl, dto);
  }

  updateTarea(id: string, dto: UpdateTareaDto): Observable<ApiResponse<TareaDto>> {
    return this.http.put<ApiResponse<TareaDto>>(`${this.apiUrl}/${id}`, dto);
  }

  cambiarEstado(id: string, dto: CambiarEstadoTareaDto): Observable<ApiResponse<TareaDto>> {
    return this.http.patch<ApiResponse<TareaDto>>(`${this.apiUrl}/${id}/estado`, dto);
  }

  deleteTarea(id: string): Observable<ApiResponse<boolean>> {
    return this.http.delete<ApiResponse<boolean>>(`${this.apiUrl}/${id}`);
  }
}
