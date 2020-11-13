using ATI.Services.Common.Behaviors;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using NLog;

namespace ATI.Services.Common.Logging
{
    [PublicAPI]
    public static class GlobalLoggingExtensions
    {
        public static void UseCustomExceptionHandler(this IApplicationBuilder app)
        {
            app.UseExceptionHandler(new ExceptionHandlerOptions
            {
                ExceptionHandler = async (context) =>
                {
                    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                    if (exception != null)
                    {
                        LogManager.GetCurrentClassLogger().ErrorWithObject(
                            exception,
                            "UNHANDLED EXCEPTION",
                            new
                            {
                                RequestPath = context.Request.Path,
                                RequestQueryParams = context.Request.QueryString,
                                RemoteHost = context.Connection.RemoteIpAddress.ToString()
                            }
                        );
                    }
                        
                    var response = context.Response;
                    response.ContentType = "application/json; charset=utf-8";
                    await response.WriteAsync(JsonConvert.SerializeObject(new ErrorResponse{ Error = "internal_error", Reason = ApiMessages.InternalServerError }));
                }
            });
        }
    }
}