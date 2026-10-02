using AsistenteJuridico.Application.Features.Expedientes.DTOs;
using AsistenteJuridico.Domain.Enums;
using FluentValidation;

namespace AsistenteJuridico.Application.Features.Expedientes.Validators;

public class CreateExpedienteValidator : AbstractValidator<CreateExpedienteDto>
{
    public CreateExpedienteValidator()
    {
        RuleFor(x => x.ClienteId)
            .NotEmpty().WithMessage("El cliente es obligatorio.");

        RuleFor(x => x.Titulo)
            .NotEmpty().WithMessage("El título del expediente es obligatorio.")
            .MaximumLength(250).WithMessage("El título no puede exceder los 250 caracteres.");

        RuleFor(x => x.Materia)
            .NotEmpty().WithMessage("La materia jurídica es obligatoria.")
            .MaximumLength(100).WithMessage("La materia no puede exceder los 100 caracteres.");
    }
}

public class UpdateExpedienteValidator : AbstractValidator<UpdateExpedienteDto>
{
    public UpdateExpedienteValidator()
    {
        RuleFor(x => x.Titulo)
            .NotEmpty().WithMessage("El título del expediente es obligatorio.")
            .MaximumLength(250).WithMessage("El título no puede exceder los 250 caracteres.");

        RuleFor(x => x.Materia)
            .NotEmpty().WithMessage("La materia jurídica es obligatoria.")
            .MaximumLength(100).WithMessage("La materia no puede exceder los 100 caracteres.");

        RuleFor(x => x.Version)
            .NotNull().WithMessage("El token de concurrencia (Version/xmin) es obligatorio.");
    }
}

public class CambiarEstadoExpedienteValidator : AbstractValidator<CambiarEstadoExpedienteDto>
{
    public CambiarEstadoExpedienteValidator()
    {
        RuleFor(x => x.NuevoEstado)
            .IsInEnum().WithMessage("Estado de expediente inválido.");

        RuleFor(x => x.Version)
            .NotNull().WithMessage("El token de concurrencia (Version/xmin) es obligatorio.");

        When(x => x.NuevoEstado == EstadoExpediente.Cerrado && x.ConfirmarCierreConTareasPendientes, () =>
        {
            RuleFor(x => x.MotivoCierreForzado)
                .NotEmpty().WithMessage("El motivo de cierre forzado es obligatorio.")
                .MinimumLength(10).WithMessage("El motivo de cierre forzado debe tener al menos 10 caracteres.");
        });
    }
}

public class VincularProcesoValidator : AbstractValidator<VincularProcesoDto>
{
    public VincularProcesoValidator()
    {
        RuleFor(x => x.ProcesoJudicialId)
            .NotEmpty().WithMessage("El identificador del proceso judicial es obligatorio.");
    }
}
