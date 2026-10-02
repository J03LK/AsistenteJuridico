import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse } from '../models/auth.models';
import { DocumentoDto } from '../models/fase4.models';

@Injectable({
  providedIn: 'root'
})
export class DocumentoService {
  private readonly http = inject(HttpClient);
  private readonly apiUrl = 'http://localhost:5270/api/v1/documentos';

  getDocumentos(expedienteId: string): Observable<ApiResponse<DocumentoDto[]>> {
    return this.http.get<ApiResponse<DocumentoDto[]>>(`${this.apiUrl}/expediente/${expedienteId}`);
  }

  getDocumentoById(id: string): Observable<ApiResponse<DocumentoDto>> {
    return this.http.get<ApiResponse<DocumentoDto>>(`${this.apiUrl}/${id}`);
  }

  uploadDocumento(
    expedienteId: string,
    titulo: string,
    file: File,
    descripcion?: string
  ): Observable<ApiResponse<DocumentoDto>> {
    const formData = new FormData();
    formData.append('expedienteId', expedienteId);
    formData.append('titulo', titulo);
    formData.append('archivo', file, file.name);
    if (descripcion) {
      formData.append('descripcion', descripcion);
    }

    return this.http.post<ApiResponse<DocumentoDto>>(`${this.apiUrl}/upload`, formData);
  }

  downloadDocumento(id: string): Observable<Blob> {
    return this.http.get(`${this.apiUrl}/${id}/download`, {
      responseType: 'blob'
    });
  }

  deleteDocumento(id: string): Observable<ApiResponse<boolean>> {
    return this.http.delete<ApiResponse<boolean>>(`${this.apiUrl}/${id}`);
  }
}
