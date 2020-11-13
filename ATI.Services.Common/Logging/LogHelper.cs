using System;
using ATI.Services.Common.Behaviors;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Hosting;
using NLog;
using NLog.Layouts;
using NLog.Web;

namespace ATI.Services.Common.Logging
{
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public class LogHelper
    {
        public static void ConfigureNlog(IWebHostEnvironment env)
        {
            try
            {
                var configPath = $"nlog.{env.EnvironmentName}.config";
                NLogBuilder.ConfigureNLog(configPath);
            }
            catch (Exception exception)
            {
                NLogBuilder.ConfigureNLog("nlog.config");
                LogManager.GetCurrentClassLogger().Error(exception);
            }
        }
        
        public static void ConfigureTracingMetricsLoggers()
        {
            var loggingConfiguration = LogManager.Configuration;

            loggingConfiguration.Variables.Add("JsonLayout", new SimpleLayout(ConfigurationManager.LoggerSettings("JsonLayout")));

            NLogBuilder.ConfigureNLog(loggingConfiguration);
        }
    }
}