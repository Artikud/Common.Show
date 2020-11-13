﻿using System.Collections.Generic;
using JetBrains.Annotations;
using Microsoft.Extensions.Options;
using NLog;

namespace ATI.Services.Common.Sql
{
    [PublicAPI]
    public class DbProvider
    {
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly Dictionary<string, DapperDb> _configuredDataBases = new Dictionary<string, DapperDb>();

        public DbProvider(IOptions<DbManagerOptions> dbManagerOptions)
        {
            foreach (var kvDataBaseOptions in dbManagerOptions.Value.DataBaseOptions)
            {
                _configuredDataBases.Add(kvDataBaseOptions.Key, new DapperDb(kvDataBaseOptions.Value));
            }
        }

        public DapperDb GetDb(string dbName)
        {
            var isDbConfigured = _configuredDataBases.TryGetValue(dbName, out var db);
            if (isDbConfigured)
            {
                return db;
            }
            _logger.Error($"В пуле нет базы {dbName}");
            return null;
        }
    }
}
