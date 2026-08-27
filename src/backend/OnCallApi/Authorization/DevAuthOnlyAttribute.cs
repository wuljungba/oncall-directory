using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace OnCallApi.Authorization;

/// <summary>
/// Hides an endpoint unless dev auth is actually switched on.
///
/// The dev-auth endpoints are anonymous and were mapped in every environment. They are
/// inert while the Development scheme is unregistered — the cookies they set are read by
/// nothing — but they are the other half of the auth bypass, and an anonymous route that
/// advertises "set your own role" should not exist in a build that does not honour it.
///
/// 404 rather than 403: an endpoint that is not part of this build should look absent.
/// </summary>
public class DevAuthOnlyAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var env = context.HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>();

        if (!config.GetValue<bool>("DevAuth:Enabled") || !env.IsDevelopment())
        {
            context.Result = new NotFoundResult();
            return;
        }

        base.OnActionExecuting(context);
    }
}
