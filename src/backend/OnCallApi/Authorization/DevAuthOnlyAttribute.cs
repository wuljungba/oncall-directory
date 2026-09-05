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
///
/// This is an <see cref="IAuthorizationFilter"/>, not an action filter, and the
/// distinction is the whole point. Authorization filters run before model binding;
/// action filters run after it. As an action filter this guard was reached only when
/// binding succeeded, so <c>[ApiController]</c>'s automatic model-state validation
/// answered <c>?role=</c> with a 400 naming the parameter it wanted — announcing the
/// route, and its shape, in exactly the build that is supposed to have no such route.
/// </summary>
public sealed class DevAuthOnlyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var env = context.HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>();

        if (!config.GetValue<bool>("DevAuth:Enabled") || !env.IsDevelopment())
        {
            context.Result = new NotFoundResult();
        }
    }
}
