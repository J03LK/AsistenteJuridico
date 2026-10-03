import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse } from '../models/auth.models';
import {
  DocumentoDto,
  DocumentoFiltro,
  PagedResponse,
  UpdateDocumentoDto
} from '../models/fase4.models';

/**
 * Nombre sugerido para guardar una descarga. Sale del modelo, no de Content-Disposition
 * (CORS no expone esa cabecera). Los documentos históricos no tienen nombreArchivoOriginal.
 */
export function nombreDescargaDocumento(documento: Pick<DocumentoDto, 'nombreArchivoOriginal' | 'titulo'>): string {
  return documento.nombreArchivoOriginal || documento.titulo;
}

@Injectable({
  providedIn: 'root'
})
export class DocumentoService {
  private readonly http = inject(HttpClient);
  private readonly apiUrl = 'http://localhost:5270/api/v1/documentos';

  getDocumentos(filtro: DocumentoFiltro): Observable<ApiResponse<PagedResponse<DocumentoDto>>> {
    let params = new HttpParams().set('expedienteId', filtro.expedienteId);

    if (filtro.pageNumber != null) {
      params = params.set('pageNumber', filtro.pageNumber.toString());
    }
    if (filtro.pageSize != null) {
      params = params.set('pageSize', filtro.pageSize.toString());
    }
    if (filtro.tipoDocumento) {
      params = params.set('tipoDocumento', filtro.tipoDocumento);
    }
    if (filtro.fechaDesde) {
      params = params.set('fechaDesde', filtro.fechaDesde);
    }
    if (filtro.fechaHasta) {
      params = params.set('fechaHasta', filtro.fechaHasta);
    }
    if (filtro.searchTerm) {
      params = params.set('searchTerm', filtro.searchTerm);
    }

    return this.http.get<ApiResponse<PagedResponse<DocumentoDto>>>(this.apiUrl, { params });
  }

  getDocumentoById(id: string): Observable<ApiResponse<DocumentoDto>> {
    return this.http.get<ApiResponse<DocumentoDto>>(`${this.apiUrl}/${id}`);
  }

  uploadDocumento(
    expedienteId: string,
    titulo: string,
    tipoDocumento: string,
    file: File,
    descripcion?: string
  ): Observable<ApiResponse<DocumentoDto>> {
    const formData = new FormData();
    formData.append('expedienteId', expedienteId);
    formData.append('titulo', titulo);
    formData.append('tipoDocumento', tipoDocumento);
    formData.append('file', file, file.name);
    if (descripcion) {
      formData.append('descripcion', descripcion);
    }

    return this.http.post<ApiResponse<DocumentoDto>>(`${this.apiUrl}/upload`, formData);
  }

  updateDocumento(id: string, dto: UpdateDocumentoDto): Observable<ApiResponse<DocumentoDto>> {
    // Se construye explícitamente: aunque dto traiga más propiedades (p. ej. un DocumentoDto), solo viajan estas 4
    const body: UpdateDocumentoDto = {
      titulo: dto.titulo,
      tipoDocumento: dto.tipoDocumento,
      descripcion: dto.descripcion ?? null,
      version: dto.version
    };

    return this.http.put<ApiResponse<DocumentoDto>>(`${this.apiUrl}/${id}`, body);
  }

  /** Devuelve solo el Blob; no lee Content-Disposition, Deprecation ni Link. */
  downloadDocumento(id: string): Observable<Blob> {
    return this.http.get(`${this.apiUrl}/${id}/download`, {
      responseType: 'blob'
    });
  }

  deleteDocumento(id: string, version: number): Observable<ApiResponse<null>> {
    const params = new HttpParams().set('version', version.toString());
    return this.http.delete<ApiResponse<null>>(`${this.apiUrl}/${id}`, { params });
  }
}
