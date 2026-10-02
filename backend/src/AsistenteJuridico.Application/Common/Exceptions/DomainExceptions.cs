namespace AsistenteJuridico.Application.Common.Exceptions;

/// <summary>
/// Excepción de dominio base. Todas las excepciones de negocio heredan de esta.
/// </summary>
public abstract class DomainException : Exception
{
    protected DomainException(string message) : base(message) { }
}

/// <summary>
/// Recurso no encontrado (HTTP 404).
/// </summary>
public class NotFoundException : DomainException
{
    public NotFoundException(string resource, object id)
        : base($"El recurso '{resource}' con identificador '{id}' no fue encontrado.") { }

    public NotFoundException(string message) : base(message) { }
}

/// <summary>
/// Acceso denegado (HTTP 403).
/// </summary>
public class ForbiddenException : DomainException
{
    public ForbiddenException(string message = "No tiene permisos para realizar esta operación.")
        : base(message) { }
}

/// <summary>
/// No autenticado o credenciales incorrectas (HTTP 401).
/// </summary>
public class UnauthorizedException : DomainException
{
    public UnauthorizedException(string message = "Credenciales incorrectas o sesión no válida.")
        : base(message) { }
}

/// <summary>
/// Cuenta bloqueada temporalmente por intentos fallidos (HTTP 423).
/// </summary>
public class UserLockedException : DomainException
{
    public UserLockedException(string message = "La cuenta se encuentra temporalmente bloqueada por exceso de intentos fallidos. Intente más tarde.")
        : base(message) { }
}

/// <summary>
/// Discrepancia de Tenant en seguridad (HTTP 403).
/// </summary>
public class TenantMismatchException : DomainException
{
    public TenantMismatchException(string message = "Violación de seguridad: El tenant especificado no coincide con el tenant autenticado.")
        : base(message) { }
}

/// <summary>
/// Conflicto de negocio — recurso duplicado (HTTP 409).
/// </summary>
public class ConflictException : DomainException
{
    public ConflictException(string message) : base(message) { }
}

/// <summary>
/// Regla de negocio violada (HTTP 422).
/// </summary>
public class BusinessRuleException : DomainException
{
    public BusinessRuleException(string message) : base(message) { }
}

/// <summary>
/// Configuración de zona horaria inválida (HTTP 422).
/// </summary>
public class TenantTimeZoneInvalidException : DomainException
{
    public TenantTimeZoneInvalidException(string message = "La zona horaria configurada para el estudio jurídico no es válida. Contacte al administrador.")
        : base(message) { }
}

/// <summary>
/// Validación de entrada fallida (HTTP 400).
/// </summary>
public class ValidationException : DomainException
{
    public IEnumerable<string> ValidationErrors { get; }

    public ValidationException(IEnumerable<string> errors)
        : base("Uno o más errores de validación ocurrieron.")
    {
        ValidationErrors = errors;
    }
}

/// <summary>
/// Documento excede el límite máximo de caracteres para análisis IA (HTTP 422).
/// </summary>
public class DocumentContextExceededException : BusinessRuleException
{
    public string ErrorCode => "DOCUMENT_EXCEEDS_CONTEXT_LIMIT";

    public DocumentContextExceededException(int length, int limit = 30000)
        : base($"DOCUMENT_EXCEEDS_CONTEXT_LIMIT: El documento contiene {length} caracteres, superando el límite máximo permitido de {limit} caracteres.") { }
}

/// <summary>
/// Contexto total excede la ventana de contexto permitida por el modelo IA (HTTP 422).
/// </summary>
public class AIContextWindowExceededException : BusinessRuleException
{
    public string ErrorCode => "AI_CONTEXT_WINDOW_EXCEEDED";

    public AIContextWindowExceededException(int estimatedTokens, int maxTokens)
        : base($"AI_CONTEXT_WINDOW_EXCEEDED: El contexto total ({estimatedTokens} tokens estimados) supera la ventana máxima del proveedor ({maxTokens} tokens).") { }
}

/// <summary>
/// Entrada del usuario excede el límite máximo de caracteres permitido (HTTP 422).
/// </summary>
public class UserInputLimitExceededException : BusinessRuleException
{
    public string ErrorCode => "USER_INPUT_LIMIT_EXCEEDED";

    public UserInputLimitExceededException(int length, int limit = 4000)
        : base($"USER_INPUT_LIMIT_EXCEEDED: La entrada del usuario ({length} caracteres) supera el límite máximo permitido de {limit} caracteres.") { }
}

/// <summary>
/// Fallo en la comunicación con el proveedor de IA externo (HTTP 502 Bad Gateway).
/// Protegido sin filtrar secretos ni cadenas de conexión.
/// </summary>
public class AIProviderException : DomainException
{
    public virtual string ErrorCode => "AI_PROVIDER_ERROR";

    public AIProviderException(string message = "El proveedor de inteligencia artificial no se encuentra disponible temporalmente. Intente nuevamente más tarde.")
        : base(message) { }
}

/// <summary>
/// El proveedor de IA externo no respondió dentro del tiempo máximo configurado (HTTP 502 Bad Gateway).
/// </summary>
public class AIProviderTimeoutException : AIProviderException
{
    public override string ErrorCode => "AI_PROVIDER_TIMEOUT";

    public AIProviderTimeoutException()
        : base("El proveedor de inteligencia artificial no respondió dentro del tiempo máximo permitido. La operación fue cancelada.") { }
}

/// <summary>
/// Exceso de cuota o tasa de peticiones (HTTP 429 Too Many Requests).
/// </summary>
public class TooManyRequestsException : DomainException
{
    public string ErrorCode => "TOO_MANY_REQUESTS";

    public TooManyRequestsException(string message = "Se ha superado la cuota o límite de tasa de solicitudes (Rate Limit). Intente más tarde.")
        : base(message) { }
}

