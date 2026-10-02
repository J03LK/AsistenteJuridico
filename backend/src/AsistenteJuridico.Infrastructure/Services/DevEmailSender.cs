using AsistenteJuridico.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;

namespace AsistenteJuridico.Infrastructure.Services;

/// <summary>
/// Proveedor de correo de desarrollo que registra las instrucciones en los logs de Serilog.
/// </summary>
public class DevEmailSender : IEmailSender
{
    private readonly ILogger<DevEmailSender> _logger;

    public DevEmailSender(ILogger<DevEmailSender> logger)
    {
        _logger = logger;
    }

    public Task SendPasswordResetEmailAsync(string toEmail, string nombreCompleto, string resetToken, string tenantSlug, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "====================================================\n" +
            "SIMULACIÓN DE ENVÍO DE EMAIL DE RECUPERACIÓN:\n" +
            "Para: {ToEmail} ({NombreCompleto})\n" +
            "Tenant: {TenantSlug}\n" +
            "Token: {Token}\n" +
            "====================================================",
            toEmail,
            nombreCompleto,
            tenantSlug,
            resetToken);

        return Task.CompletedTask;
    }
}
