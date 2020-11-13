using System.Collections.Generic;
using JetBrains.Annotations;

namespace ATI.Services.Common.Http
{
    public class HttpRequestParamsWithBody<TBody> : HttpRequestParams
    {
        /// <summary>
        /// Тело запроса
        /// </summary>
        public TBody Body { get; [PublicAPI] set; }

        [PublicAPI]
        public HttpRequestParamsWithBody()
        {
            
        }
        
        public HttpRequestParamsWithBody(
            string urlTemplate,
            string url,
            string metricName,
            TBody request,
            object parameters = null,
            Dictionary<string, string> headers = null) 
            : base(urlTemplate, url, metricName, parameters, headers)
        {
            Body = request;
        }
    }
}