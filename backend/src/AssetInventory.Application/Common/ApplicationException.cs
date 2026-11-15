namespace AssetInventory.Application.Common;

public abstract class ApplicationException(string message) : Exception(message)
{
    public abstract int StatusCode { get; }
    public abstract string Code { get; }
}

public sealed class UnauthorizedException(string message = "Неверные учетные данные.", string code = "unauthorized") : ApplicationException(message)
{
    public override int StatusCode => 401;
    public override string Code => code;
}

public sealed class NotFoundException(string message) : ApplicationException(message)
{
    public override int StatusCode => 404;
    public override string Code => "not_found";
}

public sealed class ConflictException(string message) : ApplicationException(message)
{
    public override int StatusCode => 409;
    public override string Code => "conflict";
}

public sealed class PreconditionFailedException(string message) : ApplicationException(message)
{
    public override int StatusCode => 412;
    public override string Code => "precondition_failed";
}

public sealed class PreconditionRequiredException(string message) : ApplicationException(message)
{
    public override int StatusCode => 428;
    public override string Code => "precondition_required";
}

public sealed class PasswordSetupRequiredException() : ApplicationException("Для учетной записи необходимо установить пароль.")
{
    public override int StatusCode => 409;
    public override string Code => "password_setup_required";
}

public sealed class RequestValidationException(string message) : ApplicationException(message)
{
    public override int StatusCode => 400;
    public override string Code => "validation_error";
}
