using System.Collections.Generic;
using JetBrains.Annotations;

namespace ATI.Services.Common.Http
{
    /// <summary>
    /// Если вам нужно отправить в качестве Content просто строку
    /// На данный момент необходим в SMS Service.
    /// </summary>
    public class HttpRequestParamsWithRawContent : HttpRequestParams
    {
        /// <summary>
        /// Не будет сериализоваться через JsonConvert.SerializeObject(), отправится как строка
        /// </summary>
        public string RawContent { get; [PublicAPI] set; }

        [PublicAPI]
        public HttpRequestParamsWithRawContent()
        {
            
        }

        public HttpRequestParamsWithRawContent(
            string urlTemplate, 
            string url, 
            string metricName, 
            string rawContent,
            object parameters = null,
            Dictionary<string, string> headers = null) 
            : base(urlTemplate, url, metricName, parameters, headers)
        {
            RawContent = rawContent;
        }
    }
}