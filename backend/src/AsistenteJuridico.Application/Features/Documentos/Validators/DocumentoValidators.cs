using AsistenteJuridico.Application.Features.Documentos.DTOs;
using FluentValidation;

namespace AsistenteJuridico.Application.Features.Documentos.Validators;

public class UploadDocumentoValidator : AbstractValidator<UploadDocumentoDto>
{
    public UploadDocumentoValidator()
    {
        RuleFor(x => x.ExpedienteId)
            .NotEmpty().WithMessage("El expediente es obligatorio.");

        // Fase 7.2 (G): alineado con la columna (varchar 250) y el contrato.
        RuleFor(x => x.Titulo)
            .NotEmpty().WithMessage("El título del documento es obligatorio.")
            .MaximumLength(250).WithMessage("El título no puede exceder los 250 caracteres.");

        RuleFor(x => x.TipoDocumento)
            .NotEmpty().WithMessage("El tipo de documento es obligatorio.")
            .MaximumLength(100).WithMessage("El tipo de documento no puede exceder los 100 caracteres.");

        RuleFor(x => x.Descripcion)
            .MaximumLength(1000).WithMessage("La descripción no puede exceder los 1000 caracteres.");
    }
}

/// <summary>
/// Fase 7.3 — PUT de metadatos. Las longitudes se miden sobre el valor recortado, que es el que se guarda.
/// </summary>
public class UpdateDocumentoValidator : AbstractValidator<UpdateDocumentoDto>
{
    public UpdateDocumentoValidator()
    {
        RuleFor(x => x.Titulo)
            .Must(t => !string.IsNullOrWhiteSpace(t)).WithMessage("El título del documento es obligatorio.")
            .Must(t => t == null || t.Trim().Length <= 250).WithMessage("El título no puede exceder los 250 caracteres.");

        RuleFor(x => x.TipoDocumento)
            .Must(t => !string.IsNullOrWhiteSpace(t)).WithMessage("El tipo de documento es obligatorio.")
            .Must(t => t == null || t.Trim().Length <= 100).WithMessage("El tipo de documento no puede exceder los 100 caracteres.");

        RuleFor(x => x.Descripcion)
            .Must(d => d == null || d.Trim().Length <= 1000).WithMessage("La descripción no puede exceder los 1000 caracteres.");

        // Contrato v1.1: ausente -> 400; 0 -> 400; positiva obsoleta -> 409 (lo decide el servicio con xmin).
        RuleFor(x => x.Version)
            .NotNull().WithMessage("La versión del documento es obligatoria.")
            .GreaterThan(0u).WithMessage("La versión del documento no es válida.");
    }
}

/// <summary>
/// Fase 7.3 — Filtros del listado oficial. pageNumber/pageSize los corrige PagedRequest (D73-1).
/// </summary>
public class DocumentoFilterValidator : AbstractValidator<DocumentoFilterRequest>
{
    public DocumentoFilterValidator()
    {
        RuleFor(x => x.ExpedienteId)
            .Must(id => id.HasValue && id.Value != Guid.Empty).WithMessage("El expediente es obligatorio.");

        RuleFor(x => x.TipoDocumento)
            .MaximumLength(100).WithMessage("El tipo de documento no puede exceder los 100 caracteres.");

        RuleFor(x => x.SearchTerm)
            .MaximumLength(200).WithMessage("El término de búsqueda no puede exceder los 200 caracteres.");

        RuleFor(x => x)
            .Must(f => !f.FechaDesde.HasValue || !f.FechaHasta.HasValue || f.FechaDesde.Value <= f.FechaHasta.Value)
            .WithMessage("La fecha desde no puede ser posterior a la fecha hasta.");
    }
}
