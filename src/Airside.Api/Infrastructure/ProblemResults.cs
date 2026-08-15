using Airside.Core.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Airside.Api.Infrastructure;

/// <summary>
/// Turns a domain <see cref="Error"/> into an RFC 9457 response.
/// </summary>
/// <remarks>
/// Status mapping lives here and nowhere else. Choosing a status at the call site
/// is how an API ends up returning 400 for one allocation rejection and 409 for
/// the next, and clients switch on <c>code</c> precisely so they do not have to
/// care — but only if the status is consistent enough to be ignorable.
/// </remarks>
public static class ProblemResults
{
    public static ProblemHttpResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var status = StatusFor(error.Code);

        var problem = new ProblemDetails
        {
            Type = $"https://airside.dev/errors/{error.Code}",
            Title = TitleFor(status),
            Status = status,
            Detail = error.Message,
        };

        problem.Extensions["code"] = error.Code;

        if (error.Metadata is not null)
        {
            problem.Extensions["metadata"] = error.Metadata;
        }

        return TypedResults.Problem(problem);
    }

    private static int StatusFor(string code) => code switch
    {
        ErrorCodes.AuthInvalidCredentials or ErrorCodes.AuthAccountLocked
            or ErrorCodes.AuthSetupTokenInvalid => StatusCodes.Status401Unauthorized,

        ErrorCodes.AuthPermissionDenied or ErrorCodes.WorkloadSystemProtected
            or ErrorCodes.QueryCommandRequiresElevation => StatusCodes.Status403Forbidden,

        ErrorCodes.WorkloadNotFound or ErrorCodes.JobNotFound or ErrorCodes.BackupNotFound
            or ErrorCodes.DeploymentNotFound => StatusCodes.Status404NotFound,

        ErrorCodes.ResourceInsufficientMemory or ErrorCodes.ResourceInsufficientCpu
            or ErrorCodes.ResourceInsufficientStorage or ErrorCodes.WorkloadSlugTaken
            or ErrorCodes.WorkloadInvalidTransition or ErrorCodes.WorkloadBusy
            or ErrorCodes.WorkloadConfirmationMismatch or ErrorCodes.DatabaseAttachmentExists
            or ErrorCodes.DatabaseEnvPrefixConflict or ErrorCodes.EnvironmentKeyConflict
            or ErrorCodes.DomainAlreadyBound or ErrorCodes.JobNotCancellable
            or ErrorCodes.AuthLastSuperAdmin or ErrorCodes.BackupChecksumMismatch
            or ErrorCodes.BackupEngineVersionMismatch or ErrorCodes.DeploymentImagePruned
            or ErrorCodes.QueryCommandBlocked => StatusCodes.Status409Conflict,

        ErrorCodes.RuntimeUnavailable or ErrorCodes.ProxyUnavailable
            => StatusCodes.Status503ServiceUnavailable,

        // Validation and anything unrecognised. An unmapped code returning 400
        // rather than 500 is the safer default: it says "your request was not
        // accepted", which is true, instead of claiming a server fault.
        _ => StatusCodes.Status400BadRequest,
    };

    private static string TitleFor(int status) => status switch
    {
        StatusCodes.Status401Unauthorized => "Not authenticated",
        StatusCodes.Status403Forbidden => "Not permitted",
        StatusCodes.Status404NotFound => "Not found",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status503ServiceUnavailable => "Temporarily unavailable",
        _ => "Request rejected",
    };
}

/// <summary>
/// Converts an unhandled exception into a 500 carrying only a trace id.
/// </summary>
/// <remarks>
/// No exception message, no stack trace, no inner-exception chain reaches the
/// client. On a system with root-equivalent host reach, an error message is an
/// information-disclosure surface — a connection string or a filesystem path in a
/// 500 body tells an attacker more than the failure itself does.
/// </remarks>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var traceId = httpContext.TraceIdentifier;
        logger.LogError(exception, "Unhandled exception on {Method} {Path} ({TraceId})",
            httpContext.Request.Method, httpContext.Request.Path, traceId);

        var problem = new ProblemDetails
        {
            Type = "https://airside.dev/errors/internal",
            Title = "Something went wrong",
            Status = StatusCodes.Status500InternalServerError,
            Detail = "The request could not be completed. Quote the trace id when reporting this.",
        };

        problem.Extensions["code"] = "internal.unhandled";
        problem.Extensions["traceId"] = traceId;

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken).ConfigureAwait(false);

        return true;
    }
}
