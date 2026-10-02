using AsistenteJuridico.Application.Common.Validators;
using AsistenteJuridico.Application.Features.Clientes.DTOs;
using FluentValidation;

namespace AsistenteJuridico.Application.Features.Clientes.Validators;

public class CreateClienteValidator : AbstractValidator<CreateClienteDto>
{
    public CreateClienteValidator()
    {
        RuleFor(x => x.NombreRazonSocial)
            .NotEmpty().WithMessage("El nombre o razón social es obligatorio.")
            .MaximumLength(200).WithMessage("El nombre o razón social no puede exceder los 200 caracteres.");

        RuleFor(x => x.Identificacion)
            .NotEmpty().WithMessage("La identificación es obligatoria.")
            .Custom((identificacion, context) =>
            {
                var dto = context.InstanceToValidate;
                var (isValid, errorMessage) = EcuadorianIdentityValidator.Validate(dto.TipoIdentificacion, identificacion);
                if (!isValid)
                {
                    context.AddFailure(nameof(dto.Identificacion), errorMessage ?? "Identificación inválida.");
                }
            });

        When(x => !string.IsNullOrWhiteSpace(x.Email), () =>
        {
            RuleFor(x => x.Email)
                .EmailAddress().WithMessage("El formato del correo electrónico no es válido.")
                .MaximumLength(150);
        });

        When(x => !string.IsNullOrWhiteSpace(x.Telefono), () =>
        {
            RuleFor(x => x.Telefono)
                .MaximumLength(50).WithMessage("El teléfono no puede exceder los 50 caracteres.");
        });
    }
}

public class UpdateClienteValidator : AbstractValidator<UpdateClienteDto>
{
    public UpdateClienteValidator()
    {
        RuleFor(x => x.NombreRazonSocial)
            .NotEmpty().WithMessage("El nombre o razón social es obligatorio.")
            .MaximumLength(200).WithMessage("El nombre o razón social no puede exceder los 200 caracteres.");

        RuleFor(x => x.Identificacion)
            .NotEmpty().WithMessage("La identificación es obligatoria.")
            .Custom((identificacion, context) =>
            {
                var dto = context.InstanceToValidate;
                var (isValid, errorMessage) = EcuadorianIdentityValidator.Validate(dto.TipoIdentificacion, identificacion);
                if (!isValid)
                {
                    context.AddFailure(nameof(dto.Identificacion), errorMessage ?? "Identificación inválida.");
                }
            });

        When(x => !string.IsNullOrWhiteSpace(x.Email), () =>
        {
            RuleFor(x => x.Email)
                .EmailAddress().WithMessage("El formato del correo electrónico no es válido.")
                .MaximumLength(150);
        });

        RuleFor(x => x.Version)
            .NotNull().WithMessage("El token de concurrencia (Version/xmin) es obligatorio para actualizar.");
    }
}
