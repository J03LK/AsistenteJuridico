using System.Text.Json;
using AsistenteJuridico.Application.Common.Interfaces;

namespace AsistenteJuridico.Infrastructure.Services;

/// <summary>
/// Proveedor simulado (Mock) del Consejo de la Judicatura / SATJE para desarrollo en Fase 2.
/// No realiza scraping ni llamadas externas reales.
/// </summary>
public class MockProcesoJudicialProvider : IProcesoJudicialProvider
{
    private static readonly List<ProcesoJudicialConsultaResult> MockData = new()
    {
        new ProcesoJudicialConsultaResult
        {
            NumeroProceso = "17230-2023-00456",
            Judicatura = "Unidad Judicial Civil con sede en la parroquia Iñaquito del Distrito Metropolitano de Quito, Provincia de Pichincha",
            JuezPonente = "Dr. Carlos Andrade Moreno",
            AccionInfraccion = "Cobro de Pagaré a la Orden (Procedimiento Ejecutivo)",
            Materia = "Civil y Mercantil",
            EstadoJudicial = "En Trámite - Convocatoria a Audiencia",
            FechaInicio = new DateTime(2023, 5, 14, 0, 0, 0, DateTimeKind.Utc),
            ActuacionesJson = JsonSerializer.Serialize(new[]
            {
                new { Fecha = "2023-05-14", Tipo = "Auto Inicial", Detalle = "Se califica la demanda ejecutiva por cumplir requisitos de ley." },
                new { Fecha = "2023-06-20", Tipo = "Citación", Detalle = "Citación practicada exitosamente al demandado mediante boleta judicial." },
                new { Fecha = "2023-08-10", Tipo = "Providencia", Detalle = "Se convoca a las partes procesales a Audiencia Única." }
            }),
            PartesProcesales = new List<ParteProcesalDto>
            {
                new("Actor", "Corporación Financiera Pichincha S.A.", "1790012345001"),
                new("Demandado", "Juan Alberto Pérez Morales", "1712345678")
            }
        },
        new ProcesoJudicialConsultaResult
        {
            NumeroProceso = "09332-2024-00129",
            Judicatura = "Unidad Judicial de Trabajo con sede en el Cantón Guayaquil, Provincia del Guayas",
            JuezPonente = "Dra. María Elena Suárez",
            AccionInfraccion = "Despido Intempestivo (Procedimiento Sumario)",
            Materia = "Laboral",
            EstadoJudicial = "Calificada",
            FechaInicio = new DateTime(2024, 2, 3, 0, 0, 0, DateTimeKind.Utc),
            ActuacionesJson = JsonSerializer.Serialize(new[]
            {
                new { Fecha = "2024-02-03", Tipo = "Demanda", Detalle = "Ingreso de demanda laboral en ventanilla universal de Guayaquil." }
            }),
            PartesProcesales = new List<ParteProcesalDto>
            {
                new("Actor", "Ana Lucía Caicedo Mendoza", "0923456789"),
                new("Demandado", "Distribuidora del Pacífico Cía. Ltda.", "0991234567001")
            }
        }
    };

    public Task<ProcesoJudicialConsultaResult?> ConsultarPorNumeroAsync(string numeroProceso, CancellationToken cancellationToken = default)
    {
        var cleanNumero = numeroProceso.Trim().Replace(" ", "");
        var match = MockData.FirstOrDefault(p => p.NumeroProceso.Replace("-", "").Equals(cleanNumero.Replace("-", ""), StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(match);
    }

    public Task<IReadOnlyList<ProcesoJudicialConsultaResult>> ConsultarPorIdentificacionAsync(string tipoIdentificacion, string identificacion, CancellationToken cancellationToken = default)
    {
        var cleanId = identificacion.Trim();
        var matches = MockData
            .Where(p => p.PartesProcesales.Any(parte => parte.Identificacion == cleanId))
            .ToList();

        return Task.FromResult<IReadOnlyList<ProcesoJudicialConsultaResult>>(matches);
    }
}
