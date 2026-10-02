import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse } from '../models/auth.models';
import { ClienteDto, CreateClienteDto, PagedResponse, UpdateClienteDto } from '../models/fase4.models';

@Injectable({
  providedIn: 'root'
})
export class ClienteService {
  private readonly http = inject(HttpClient);
  private readonly apiUrl = 'http://localhost:5270/api/v1/clientes';

  getClientes(pageNumber = 1, pageSize = 10, search?: string, activo?: boolean): Observable<ApiResponse<PagedResponse<ClienteDto>>> {
    let params = new HttpParams()
      .set('pageNumber', pageNumber.toString())
      .set('pageSize', pageSize.toString());

    if (search) {
      params = params.set('search', search);
    }
    if (activo !== undefined && activo !== null) {
      params = params.set('activo', activo.toString());
    }

    return this.http.get<ApiResponse<PagedResponse<ClienteDto>>>(this.apiUrl, { params });
  }

  getClienteById(id: string): Observable<ApiResponse<ClienteDto>> {
    return this.http.get<ApiResponse<ClienteDto>>(`${this.apiUrl}/${id}`);
  }

  createCliente(dto: CreateClienteDto): Observable<ApiResponse<ClienteDto>> {
    return this.http.post<ApiResponse<ClienteDto>>(this.apiUrl, dto);
  }

  updateCliente(id: string, dto: UpdateClienteDto): Observable<ApiResponse<ClienteDto>> {
    return this.http.put<ApiResponse<ClienteDto>>(`${this.apiUrl}/${id}`, dto);
  }

  deleteCliente(id: string): Observable<ApiResponse<boolean>> {
    return this.http.delete<ApiResponse<boolean>>(`${this.apiUrl}/${id}`);
  }
}
