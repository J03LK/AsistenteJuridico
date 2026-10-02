import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse } from '../models/auth.models';
import { PagedResponse, ProcesoJudicialDto } from '../models/fase4.models';

@Injectable({
  providedIn: 'root'
})
export class ProcesoJudicialService {
  private readonly http = inject(HttpClient);
  private readonly apiUrl = 'http://localhost:5270/api/v1/procesos-judiciales';

  buscarCausas(
    pageNumber = 1,
    pageSize = 10,
    numeroProceso?: string,
    judicatura?: string,
    actorDemandado?: string
  ): Observable<ApiResponse<PagedResponse<ProcesoJudicialDto>>> {
    let params = new HttpParams()
      .set('pageNumber', pageNumber.toString())
      .set('pageSize', pageSize.toString());

    if (numeroProceso) {
      params = params.set('numeroProceso', numeroProceso);
    }
    if (judicatura) {
      params = params.set('judicatura', judicatura);
    }
    if (actorDemandado) {
      params = params.set('actorDemandado', actorDemandado);
    }

    return this.http.get<ApiResponse<PagedResponse<ProcesoJudicialDto>>>(this.apiUrl, { params });
  }

  getProcesoById(id: string): Observable<ApiResponse<ProcesoJudicialDto>> {
    return this.http.get<ApiResponse<ProcesoJudicialDto>>(`${this.apiUrl}/${id}`);
  }

  sincronizarMock(numeroProceso: string): Observable<ApiResponse<ProcesoJudicialDto>> {
    return this.http.post<ApiResponse<ProcesoJudicialDto>>(`${this.apiUrl}/sincronizar-mock`, { numeroProceso });
  }
}
