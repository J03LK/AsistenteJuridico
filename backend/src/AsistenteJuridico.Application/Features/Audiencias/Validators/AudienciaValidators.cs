using AsistenteJuridico.Application.Features.Audiencias.DTOs;
using FluentValidation;

namespace AsistenteJuridico.Application.Features.Audiencias.Validators;

public class CreateAudienciaValidator : AbstractValidator<CreateAudienciaDto>
{
    public CreateAudienciaValidator()
    {
        RuleFor(x => x.ExpedienteId)
            .NotEmpty().WithMessage("El expediente es obligatorio.");

        RuleFor(x => x.FechaHora)
            .NotEmpty().WithMessage("La fecha y hora de la audiencia es obligatoria.");

        RuleFor(x => x.SalaOVirtual)
            .NotEmpty().WithMessage("La sala o enlace virtual es obligatorio.")
            .MaximumLength(200).WithMessage("La ubicación no puede exceder los 200 caracteres.");
    }
}

public class UpdateAudienciaValidator : AbstractValidator<UpdateAudienciaDto>
{
    public UpdateAudienciaValidator()
    {
        RuleFor(x => x.FechaHora)
            .NotEmpty().WithMessage("La fecha y hora de la audiencia es obligatoria.");

        RuleFor(x => x.SalaOVirtual)
            .NotEmpty().WithMessage("La sala o enlace virtual es obligatorio.")
            .MaximumLength(200).WithMessage("La ubicación no puede exceder los 200 caracteres.");

        RuleFor(x => x.Version)
            .NotNull().WithMessage("El token de concurrencia (Version/xmin) es obligatorio.");
    }
}

public class CambiarEstadoAudienciaValidator : AbstractValidator<CambiarEstadoAudienciaDto>
{
    public CambiarEstadoAudienciaValidator()
    {
        RuleFor(x => x.NuevoEstado)
            .IsInEnum().WithMessage("Estado de audiencia inválido.");

        RuleFor(x => x.Version)
            .NotNull().WithMessage("El token de concurrencia (Version/xmin) es obligatorio.");
    }
}
