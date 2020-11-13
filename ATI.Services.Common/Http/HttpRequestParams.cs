using System.Collections.Generic;
using JetBrains.Annotations;

namespace ATI.Services.Common.Http
{
    public class HttpRequestParams
    {
        /// <summary>
        /// Шаблон урла, к примеру, /accounts/{0}. Используется в метриках
        /// </summary>
        public string UrlTemplate { get; [PublicAPI] set; } 
        
        /// <summary>
        /// Относительный путь к методу, например, /accounts/14612
        /// </summary>
        public string Url { get; [PublicAPI] set; }
        
        /// <summary>
        /// Название метрики
        /// </summary>
        public string MetricName { get; [PublicAPI] set; }
        
        /// <summary>
        /// Параметры запроса. Используются только для логирования
        /// </summary>
        public object Parameters { get; [PublicAPI] set; }
        
        /// <summary>
        /// Дополнительные заголовки к запросу
        /// </summary>
        public Dictionary<string, string> Headers { get; [PublicAPI] set; }
        
        [PublicAPI]
        public HttpRequestParams()
        {
            
        }
        
        public HttpRequestParams(
            string urlTemplate, 
            string url, 
            string metricName, 
            object parameters = null,
            Dictionary<string, string> headers = null)
        {
            UrlTemplate = urlTemplate;
            Url = url;
            MetricName = metricName;
            Parameters = parameters;
            Headers = headers;
        }
    }
}