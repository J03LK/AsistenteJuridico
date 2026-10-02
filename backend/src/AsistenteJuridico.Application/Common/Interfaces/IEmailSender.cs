namespace AsistenteJuridico.Application.Common.Interfaces;

public interface IEmailSender
{
    Task SendPasswordResetEmailAsync(string toEmail, string nombreCompleto, string resetToken, string tenantSlug, CancellationToken ct = default);
}
