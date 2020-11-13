using ATI.Services.Common.Behaviors;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ATI.Services.Common.Api.Attributes
{
    [PublicAPI]
    public class ValidateModelStateAttribute : ActionFilterAttribute
    {
        public override void OnActionExecuting(ActionExecutingContext actionContext)
        {
            if (!actionContext.ModelState.IsValid)
            {
                actionContext.Result = CommonBehavior.GetActionResult(ActionStatus.BadRequest, actionContext.ModelState);

                base.OnActionExecuting(actionContext);
            }
        }
    }
}