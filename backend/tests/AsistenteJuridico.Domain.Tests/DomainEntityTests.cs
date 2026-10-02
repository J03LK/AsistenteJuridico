using AsistenteJuridico.Domain.Common;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using Xunit;

namespace AsistenteJuridico.Domain.Tests;

public class DomainEntityTests
{
    [Fact]
    public void BaseEntity_ShouldInitializeWithNonEmptyGuid()
    {
        // Act
        var tenant = new Tenant();
        var cliente = new Cliente();
        var expediente = new Expediente();

        // Assert
        Assert.NotEqual(Guid.Empty, tenant.Id);
        Assert.NotEqual(Guid.Empty, cliente.Id);
        Assert.NotEqual(Guid.Empty, expediente.Id);
    }

    [Fact]
    public void MultiTenantEntities_ShouldImplementIMultiTenant()
    {
        // Arrange
        var tenantId = Guid.NewGuid();

        // Act & Assert
        var cliente = new Cliente { TenantId = tenantId };
        var expediente = new Expediente { TenantId = tenantId };
        var procesoJudicial = new ProcesoJudicial { TenantId = tenantId };
        var documento = new Documento { TenantId = tenantId };
        var tarea = new Tarea { TenantId = tenantId };
        var audiencia = new Audiencia { TenantId = tenantId };
        var auditoria = new HistorialAuditoria { TenantId = tenantId };

        Assert.IsAssignableFrom<IMultiTenant>(cliente);
        Assert.IsAssignableFrom<IMultiTenant>(expediente);
        Assert.IsAssignableFrom<IMultiTenant>(procesoJudicial);
        Assert.IsAssignableFrom<IMultiTenant>(documento);
        Assert.IsAssignableFrom<IMultiTenant>(tarea);
        Assert.IsAssignableFrom<IMultiTenant>(audiencia);
        Assert.IsAssignableFrom<IMultiTenant>(auditoria);

        Assert.Equal(tenantId, cliente.TenantId);
        Assert.Equal(tenantId, expediente.TenantId);
        Assert.Equal(tenantId, procesoJudicial.TenantId);
    }

    [Fact]
    public void SoftDeletableEntities_ShouldInitializeWithIsDeletedFalse()
    {
        // Act
        var cliente = new Cliente();
        var expediente = new Expediente();
        var documento = new Documento();

        // Assert
        Assert.False(cliente.IsDeleted);
        Assert.Null(cliente.DeletedAt);
        Assert.False(expediente.IsDeleted);
        Assert.Null(expediente.DeletedAt);
        Assert.False(documento.IsDeleted);
        Assert.Null(documento.DeletedAt);
    }

    [Fact]
    public void ExpedienteAndProcesoJudicial_ShouldBeDecoupledAndLinkedViaJunctionEntity()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var expediente = new Expediente
        {
            TenantId = tenantId,
            NumeroExpediente = "EXP-2026-0001",
            Titulo = "Caso Arbitral Corporativo",
            Estado = EstadoExpediente.EnTramite
        };

        var procesoJudicial = new ProcesoJudicial
        {
            TenantId = tenantId,
            NumeroProceso = "17230-2023-00456",
            Judicatura = "Unidad Judicial Civil de Iñaquito",
            Materia = "Civil"
        };

        // Act
        var vinculo = new ExpedienteProcesoJudicial
        {
            TenantId = tenantId,
            ExpedienteId = expediente.Id,
            Expediente = expediente,
            ProcesoJudicialId = procesoJudicial.Id,
            ProcesoJudicial = procesoJudicial,
            EsPrincipal = true
        };

        expediente.ProcesosVinculados.Add(vinculo);
        procesoJudicial.ExpedientesVinculados.Add(vinculo);

        // Assert
        Assert.Single(expediente.ProcesosVinculados);
        Assert.Equal("17230-2023-00456", expediente.ProcesosVinculados.First().ProcesoJudicial.NumeroProceso);
        Assert.Equal("EXP-2026-0001", procesoJudicial.ExpedientesVinculados.First().Expediente.NumeroExpediente);
        Assert.True(vinculo.EsPrincipal);
    }

    [Fact]
    public void Documento_ShouldHaveInitialPendingStateForFutureAiRag()
    {
        // Act
        var doc = new Documento
        {
            Titulo = "Demanda Inicial.pdf",
            RutaAlmacenamiento = "uploads/demanda.pdf",
            ContentType = "application/pdf",
            TamanioBytes = 102400
        };

        // Assert
        Assert.Equal(EstadoProcesamientoIa.Pendiente, doc.EstadoIa);
        Assert.False(doc.IsDeleted);
    }
}
