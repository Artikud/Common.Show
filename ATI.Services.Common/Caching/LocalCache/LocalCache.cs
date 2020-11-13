﻿using System;
using System.Threading;
using System.Threading.Tasks;
using ATI.Services.Common.Initializers;
using ATI.Services.Common.Initializers.Interfaces;
using NLog;

namespace ATI.Services.Common.Caching.LocalCache
{
    [InitializeOrder(Order = InitializeOrder.Fourth)]
    public abstract class LocalCache<T> : ILocalCache
        where T : class
    {
        private readonly string _typeName;
        private readonly ILogger _logger;
        private readonly TimeSpan _reloadPeriodAfterInitialize;
        private readonly TimeSpan _reloadPeriodOnInitialize;
        private Timer _reloadTimer;
        private bool _initialized;
        private T _value;

        protected LocalCache(TimeSpan reloadPeriodAfterInitialize, TimeSpan reloadPeriodOnInitialize)
        {
            _typeName = GetType().Name;
            _logger = LogManager.GetLogger(_typeName);
            _reloadPeriodAfterInitialize = reloadPeriodAfterInitialize;
            _reloadPeriodOnInitialize = reloadPeriodOnInitialize;
            _reloadTimer = null;
        }

        public async Task<bool> TryReloadAsync()
        {
            try
            {
                _logger.Trace($"{_typeName}.TryReloadAsync started");
                var fromDb = await GetFromDbAsync();
                if (fromDb != null)
                {
                    _value = fromDb;
                }

                _logger.Trace($"{_typeName}.TryReloadAsync finished, result={fromDb != null}");
                return fromDb != null;
            }
            catch (Exception exception)
            {
                _logger.Error(exception, $"Не смогли сделать {_typeName}.{nameof(TryReloadAsync)}");
                return false;
            }
        }

        protected abstract Task<T> GetFromDbAsync();

        protected T GetCachedValue()
        {
            if (_initialized == false)
            {
                throw new InvalidOperationException($"{GetType()} doesn't _initialized yet.");
            }
            return _value;
        }

        async Task IInitializer.InitializeAsync()
        {
            _logger.Trace($"{_typeName}.InitializeAsync started");

            _reloadTimer?.Dispose();

            if (await TryReloadAsync())
            {
                _initialized = true;
                _reloadTimer = new Timer(async state => await TryReloadAsync(), null, _reloadPeriodAfterInitialize, _reloadPeriodAfterInitialize);
            }
            else
            {
                await Task.Factory.StartNew(async () =>
                {
                    do
                    {
                        LogManager.GetLogger(nameof(LocalCache<T>)).Trace("INIT_ Cache " + typeof(T).Name + ": reload async returned false. Trying it again.");
                        await Task.Delay(_reloadPeriodOnInitialize);
                    } while (await TryReloadAsync() == false);

                    _initialized = true;
                    _reloadTimer = new Timer(async state => await TryReloadAsync(), null, _reloadPeriodAfterInitialize, _reloadPeriodAfterInitialize);
                });
            }
            _logger.Trace($"{_typeName}.InitializeAsync finished");
        }

        void IDisposable.Dispose()
        {
            _reloadTimer?.Dispose();
        }
    }
}
