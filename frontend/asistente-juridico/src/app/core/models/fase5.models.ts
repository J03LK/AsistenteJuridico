export interface KpisGeneralesDto {
  totalExpedientesActivos: number;
  totalTareasPendientes: number;
  totalAudienciasProximas7Dias: number;
  totalAlertasAltaCriticasSinResolver: number;
}

export interface DistribucionEstadoDto {
  abiertos: number;
  enTramite: number;
  suspendidos: number;
  cerrados: number;
  archivados: number;
}

export interface TareasPendientesPorPrioridadDto {
  baja: number;
  media: number;
  alta: number;
  urgente: number;
}

export interface ProximaAudienciaDto {
  id: string;
  titulo: string;
  fechaHora: string;
  salaOVirtual: string;
  expedienteNumero?: string;
  abogadoNombre?: string;
}

export interface MetricasInactividadDto {
  expedientesInactivos30Dias: number;
  expedientesInactivos60Dias: number;
}

export interface MetricasEficienciaDto {
  totalExpedientesCerradosEvaluados: number;
  expedientesCerradosATiempo: number;
  expedientesConPlazoEstimado: number;
  porcentajeCumplimiento: number | null;
}

export interface DashboardResumenDto {
  kpis: KpisGeneralesDto;
  distribucionExpedientes: DistribucionEstadoDto;
  tareasPendientesPorPrioridad: TareasPendientesPorPrioridadDto;
  proximasAudiencias: ProximaAudienciaDto[];
  inactividad: MetricasInactividadDto;
  eficiencia: MetricasEficienciaDto;
}

export interface EventoAgendaDto {
  id: string;
  tipoEvento: number; // 1: Audiencia, 2: Tarea
  titulo: string;
  descripcion?: string;
  fechaHoraInicioUtc: string;
  fechaHoraFinUtc?: string;
  ubicacionOSala?: string;
  estado: number;
  prioridad?: string;
  expedienteId?: string;
  expedienteNumero?: string;
  expedienteTitulo?: string;
  materia?: string;
  responsableId?: string;
  responsableNombre?: string;
}

export interface AgendaFilterRequest {
  fechaDesdeUtc: string;
  fechaHastaUtc: string;
  abogadoId?: string;
  materia?: string;
  tipoEvento?: number;
  pageNumber?: number;
  pageSize?: number;
}

export interface AgendaPaginadaDto {
  items: EventoAgendaDto[];
  totalCount: number;
  pageNumber: number;
  pageSize: number;
}

export interface AlertaProcesalDto {
  id: string;
  tenantId: string;
  usuarioId?: string;
  usuarioNombre?: string;
  tipoOrigen: number;
  origenId: string;
  reglaAlerta: number;
  fechaObjetivoUtc: string;
  fechaDisparoUtc: string;
  titulo: string;
  mensaje: string;
  severidad: number; // 1: Baja, 2: Media, 3: Alta, 4: Critica
  expedienteId?: string;
  expedienteNumero?: string;
  estadoResolucion: number; // 1: Activa, 2: Resuelta, 3: Invalida, 4: Descartada
  resueltaUtc?: string;
  motivoResolucion?: string;
  leida: boolean;
  fechaLeidaUtc?: string;
  version: number;
}

export interface AlertasFilterRequest {
  soloNoLeidas?: boolean;
  incluirResueltas?: boolean;
  pageNumber?: number;
  pageSize?: number;
}

export interface ConteoNoLeidasDto {
  totalNoLeidas: number;
}

export interface MarcarTodasLeidasRequest {
  incluirInstitucionales?: boolean;
}

export interface MarcarTodasLeidasResponseDto {
  totalAfectadas: number;
}
