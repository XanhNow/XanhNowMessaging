namespace XanhNow.Messaging.Api;

public sealed class MessagingSecurityBoundaryRequiredMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(
                "/health", StringComparison.OrdinalIgnoreCase) ||
            context.User.Identity?.IsAuthenticated == true)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new
        {
            code = "messaging.security_boundary_required",
            message = "Messaging API requests must be authenticated and forwarded by XanhNow Security."
        }, context.RequestAborted);
    }
}
