using AsistenteJuridico.Application.Features.Documentos.DTOs;
using FluentValidation;

namespace AsistenteJuridico.Application.Features.Documentos.Validators;

public class UploadDocumentoValidator : AbstractValidator<UploadDocumentoDto>
{
    public UploadDocumentoValidator()
    {
        RuleFor(x => x.ExpedienteId)
            .NotEmpty().WithMessage("El expediente es obligatorio.");

        RuleFor(x => x.Titulo)
            .NotEmpty().WithMessage("El título del documento es obligatorio.")
            .MaximumLength(200).WithMessage("El título no puede exceder los 200 caracteres.");

        RuleFor(x => x.TipoDocumento)
            .NotEmpty().WithMessage("El tipo de documento es obligatorio.")
            .MaximumLength(100).WithMessage("El tipo de documento no puede exceder los 100 caracteres.");
    }
}
