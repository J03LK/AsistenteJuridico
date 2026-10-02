export interface PagedResponse<T> {
  items: T[];
  pageNumber: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  hasPreviousPage: boolean;
  hasNextPage: boolean;
}

export type TipoIdentificacion = 'Cedula' | 'Ruc' | 'Pasaporte' | 'Otro';

export interface ClienteDto {
  id: string;
  tenantId: string;
  tipoIdentificacion: TipoIdentificacion;
  identificacion: string;
  nombreRazonSocial: string;
  email?: string;
  telefono?: string;
  direccion?: string;
  ciudad?: string;
  esPersonaJuridica: boolean;
  activo: boolean;
  createdAt: string;
  updatedAt?: string;
  version: number;
}

export interface CreateClienteDto {
  tipoIdentificacion: TipoIdentificacion;
  identificacion: string;
  nombreRazonSocial: string;
  email?: string;
  telefono?: string;
  direccion?: string;
  ciudad?: string;
  esPersonaJuridica: boolean;
}

export interface UpdateClienteDto {
  nombreRazonSocial: string;
  email?: string;
  telefono?: string;
  direccion?: string;
  ciudad?: string;
  activo: boolean;
  version: number;
}

export type EstadoExpediente = 'Abierto' | 'EnTramite' | 'Suspendido' | 'Cerrado' | 'Archivado';
export type PrioridadExpediente = 'Baja' | 'Media' | 'Alta' | 'Urgente';

export interface ExpedienteDto {
  id: string;
  numeroExpediente: string;
  titulo: string;
  descripcion?: string;
  materia: string;
  estado: EstadoExpediente;
  prioridad: PrioridadExpediente;
  clienteId: string;
  clienteNombre: string;
  abogadoResponsableId?: string;
  abogadoResponsableNombre?: string;
  fechaApertura: string;
  fechaCierreReal?: string;
  createdAt: string;
  version: number;
}

export interface ExpedienteDetailDto extends ExpedienteDto {
  observaciones?: string;
  tareas: TareaDto[];
  audiencias: AudienciaDto[];
  documentos: DocumentoDto[];
  procesosJudiciales: ExpedienteProcesoJudicialDto[];
}

export interface CreateExpedienteDto {
  titulo: string;
  descripcion?: string;
  materia: string;
  prioridad: PrioridadExpediente;
  clienteId: string;
  abogadoResponsableId?: string;
  observaciones?: string;
}

export interface UpdateExpedienteDto {
  titulo: string;
  descripcion?: string;
  materia: string;
  prioridad: PrioridadExpediente;
  abogadoResponsableId?: string;
  observaciones?: string;
  version: number;
}

export interface CambiarEstadoExpedienteDto {
  nuevoEstado: EstadoExpediente;
  confirmarCierreConTareasPendientes: boolean;
  motivoCierreForzado?: string;
  version: number;
}

export interface ProcesoJudicialDto {
  id: string;
  numeroProceso: string;
  judicatura: string;
  materia: string;
  tipoAccion?: string;
  actor?: string;
  demandado?: string;
  estadoProceso?: string;
  fechaUltimaActuacion?: string;
  ultimaActuacionResumen?: string;
  ultimaSincronizacionMock: string;
  esMock: boolean;
  version: number;
}

export interface ExpedienteProcesoJudicialDto {
  id: string;
  expedienteId: string;
  procesoJudicialId: string;
  numeroProceso: string;
  judicatura: string;
  materia: string;
  esPrincipal: boolean;
  fechaVinculacion: string;
  observaciones?: string;
}

export interface VincularProcesoDto {
  procesoJudicialId: string;
  esPrincipal: boolean;
  observaciones?: string;
}

export type EstadoTarea = 'Pendiente' | 'EnProgreso' | 'Completada' | 'Cancelada';
export type PrioridadTarea = 'Baja' | 'Media' | 'Alta' | 'Urgente';

export interface TareaDto {
  id: string;
  expedienteId: string;
  expedienteNumero?: string;
  titulo: string;
  descripcion?: string;
  estado: EstadoTarea;
  prioridad: PrioridadTarea;
  asignadoAId?: string;
  asignadoANombre?: string;
  fechaLimite?: string;
  createdAt: string;
  updatedAt?: string;
  version: number;
}

export interface CreateTareaDto {
  expedienteId: string;
  titulo: string;
  descripcion?: string;
  prioridad: PrioridadTarea;
  asignadoAId?: string;
  fechaLimite?: string;
}

export interface UpdateTareaDto {
  titulo: string;
  descripcion?: string;
  prioridad: PrioridadTarea;
  asignadoAId?: string;
  fechaLimite?: string;
  version: number;
}

export interface CambiarEstadoTareaDto {
  nuevoEstado: EstadoTarea;
  version: number;
}

export type EstadoAudiencia = 'Programada' | 'Realizada' | 'Suspendida' | 'Cancelada';

export interface AudienciaDto {
  id: string;
  expedienteId: string;
  expedienteNumero?: string;
  procesoJudicialId?: string;
  numeroProceso?: string;
  fechaHora: string;
  salaJudicial?: string;
  juezMagistrado?: string;
  tipoAudiencia?: string;
  estado: EstadoAudiencia;
  enlaceVirtual?: string;
  resultadoObservaciones?: string;
  createdAt: string;
  version: number;
}

export interface CreateAudienciaDto {
  expedienteId: string;
  procesoJudicialId?: string;
  fechaHora: string;
  salaJudicial?: string;
  juezMagistrado?: string;
  tipoAudiencia?: string;
  enlaceVirtual?: string;
  resultadoObservaciones?: string;
}

export interface UpdateAudienciaDto {
  fechaHora: string;
  salaJudicial?: string;
  juezMagistrado?: string;
  tipoAudiencia?: string;
  estado: EstadoAudiencia;
  enlaceVirtual?: string;
  resultadoObservaciones?: string;
  version: number;
}

export interface DocumentoDto {
  id: string;
  expedienteId: string;
  titulo: string;
  descripcion?: string;
  nombreArchivoOriginal: string;
  contentType: string;
  tamanoBytes: number;
  sha256Hash: string;
  versionDocumento: number;
  uploadedBy: string;
  createdAt: string;
  version: number;
}
