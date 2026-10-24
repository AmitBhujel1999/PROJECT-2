namespace Accounting.Core;

/// <summary>
/// A rule violation the user can fix (bad discount, not enough stock, ...).
/// <see cref="Code"/> and <see cref="StatusCode"/> match the old Django API
/// (INVALID_DISCOUNT, INSUFFICIENT_STOCK / 409, ...) so the phone app keeps working.
/// </summary>
public class BusinessError(string message, string code = "BUSINESS_RULE_VIOLATION", int statusCode = 400,
    IReadOnlyDictionary<string, object?>? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public IReadOnlyDictionary<string, object?> Details { get; } = details ?? new Dictionary<string, object?>();
}

public class CalculationError(string message, string code = "INVALID_AMOUNT") : BusinessError(message, code);

public class InsufficientStockError(string message, IReadOnlyDictionary<string, object?> details)
    : BusinessError(message, "INSUFFICIENT_STOCK", 409, details);

/// <summary>Input that fails field validation; <see cref="BusinessError.Details"/> maps field name to message.</summary>
public class ValidationError(string field, string message)
    : BusinessError(message, "VALIDATION_ERROR", 400, new Dictionary<string, object?> { [field] = message })
{
    public string Field { get; } = field;
}

public class NotFoundError(string message) : BusinessError(message, "NOT_FOUND", 404);

public class PermissionDeniedError() : BusinessError("You do not have permission to perform this action.", "PERMISSION_DENIED", 403);
