import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse } from '../models/auth.models';
import { AgendaFilterRequest, AgendaPaginadaDto, EventoAgendaDto } from '../models/fase5.models';

@Injectable({
  providedIn: 'root'
})
export class AgendaService {
  private readonly http = inject(HttpClient);
  private readonly apiUrl = 'http://localhost:5270/api/v1/agenda';

  getEventos(filter: AgendaFilterRequest): Observable<ApiResponse<AgendaPaginadaDto>> {
    let params = new HttpParams()
      .set('fechaDesdeUtc', filter.fechaDesdeUtc)
      .set('fechaHastaUtc', filter.fechaHastaUtc);

    if (filter.abogadoId) params = params.set('abogadoId', filter.abogadoId);
    if (filter.materia) params = params.set('materia', filter.materia);
    if (filter.tipoEvento !== undefined && filter.tipoEvento !== null) {
      params = params.set('tipoEvento', filter.tipoEvento.toString());
    }
    if (filter.pageNumber) params = params.set('pageNumber', filter.pageNumber.toString());
    if (filter.pageSize) params = params.set('pageSize', filter.pageSize.toString());

    return this.http.get<ApiResponse<AgendaPaginadaDto>>(`${this.apiUrl}/eventos`, { params });
  }

  getHoy(): Observable<ApiResponse<EventoAgendaDto[]>> {
    return this.http.get<ApiResponse<EventoAgendaDto[]>>(`${this.apiUrl}/hoy`);
  }
}
