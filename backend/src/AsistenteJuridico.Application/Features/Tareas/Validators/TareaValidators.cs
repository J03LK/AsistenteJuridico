using AsistenteJuridico.Application.Features.Tareas.DTOs;
using FluentValidation;

namespace AsistenteJuridico.Application.Features.Tareas.Validators;

public class CreateTareaValidator : AbstractValidator<CreateTareaDto>
{
    public CreateTareaValidator()
    {
        RuleFor(x => x.ExpedienteId)
            .NotEmpty().WithMessage("El expediente es obligatorio.");

        RuleFor(x => x.Titulo)
            .NotEmpty().WithMessage("El título de la tarea es obligatorio.")
            .MaximumLength(200).WithMessage("El título no puede exceder los 200 caracteres.");

        RuleFor(x => x.FechaVencimiento)
            .NotEmpty().WithMessage("La fecha de vencimiento es obligatoria.");
    }
}

public class UpdateTareaValidator : AbstractValidator<UpdateTareaDto>
{
    public UpdateTareaValidator()
    {
        RuleFor(x => x.Titulo)
            .NotEmpty().WithMessage("El título de la tarea es obligatorio.")
            .MaximumLength(200).WithMessage("El título no puede exceder los 200 caracteres.");

        RuleFor(x => x.FechaVencimiento)
            .NotEmpty().WithMessage("La fecha de vencimiento es obligatoria.");

        RuleFor(x => x.Version)
            .NotNull().WithMessage("El token de concurrencia (Version/xmin) es obligatorio.");
    }
}

public class CambiarEstadoTareaValidator : AbstractValidator<CambiarEstadoTareaDto>
{
    public CambiarEstadoTareaValidator()
    {
        RuleFor(x => x.NuevoEstado)
            .IsInEnum().WithMessage("Estado de tarea no válido.");

        RuleFor(x => x.Version)
            .NotNull().WithMessage("El token de concurrencia (Version/xmin) es obligatorio.");
    }
}
