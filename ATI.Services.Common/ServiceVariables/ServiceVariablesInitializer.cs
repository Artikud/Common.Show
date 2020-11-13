﻿using System.Collections.Generic;
using System.Threading.Tasks;
using ATI.Services.Common.Initializers;
using ATI.Services.Common.Initializers.Interfaces;
using Microsoft.Extensions.Options;

namespace ATI.Services.Common.ServiceVariables
{
    [InitializeOrder(Order = InitializeOrder.First)]
    public class ServiceVariablesInitializer : IInitializer
    {
        private static bool _initialized;
        private readonly ServiceVariablesOptions _options;

        public ServiceVariablesInitializer(IOptions<ServiceVariablesOptions> options)
        {
            _options = options.Value;
        }

        public Task InitializeAsync()
        {
            if (_initialized)
            {
                return Task.CompletedTask;
            }

            ServiceVariables.Variables = _options?.Variables ?? new Dictionary<string, string>();
            ServiceVariables.ServiceAsClientName = ServiceVariables.Variables.TryGetValue("ServiceAsClientName", out var name) ? name : "";

            _initialized = true;
            return Task.CompletedTask;
        }
    }
}
