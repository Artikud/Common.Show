using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ATI.Services.Common.Behaviors;
using ATI.Services.Common.Extensions;
using ATI.Services.Common.Logging;
using ATI.Services.Common.Metrics;
using ATI.Services.Common.Tracing;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NLog;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using StackExchange.Redis;

namespace ATI.Services.Common.Caching.Redis
{
    [PublicAPI]
    public class RedisCache
    {
        private readonly ILogger _logger = LogManager.GetCurrentClassLogger();
        private IDatabase _redisDb;

        private readonly CircuitBreakerPolicy _circuitBreakerPolicy;
        private readonly Policy _policy;
        private readonly HitRatioCounter _counter;
        private readonly RedisOptions _options;
        private readonly MetricsTracingFactory _metricsTracingFactory;
        private readonly JsonSerializerSettings _serializeSettings;
        private bool _connected;

        private static readonly Policy<ConnectionMultiplexer> InitPolicy =
            Policy<ConnectionMultiplexer>
                .Handle<Exception>()
                .OrResult(res => res == null)
                .WaitAndRetryForeverAsync(_ => TimeSpan.FromSeconds(30));

        public RedisCache(RedisOptions options, CacheHitRatioManager manager)
        {
            _options = options;
            _metricsTracingFactory =
                MetricsTracingFactory.CreateRedisMetricsFactory(nameof(RedisCache), _options.LongRequestTime);
            _serializeSettings = new JsonSerializerSettings
            {
                ContractResolver = new DefaultContractResolver {IgnoreShouldSerializeMembers = true}
            };
            _circuitBreakerPolicy = Policy.Handle<Exception>()
                .CircuitBreakerAsync(_options.CircuitBreakerExceptionsCount, _options.CircuitBreakerSeconds);
            _policy = Policy.WrapAsync(Policy.TimeoutAsync(_options.RedisTimeout, TimeoutStrategy.Pessimistic),
                _circuitBreakerPolicy);
            _counter = manager.CreateCounter(nameof(RedisCache));
        }

        public async Task InitAsync()
        {
            if (_options.MustConnectOnInit)
                await ConnectToRedisAsync();
            else
                ConnectToRedisAsync().Forget();
        }

        private async Task ConnectToRedisAsync()
        {
            try
            {
                var connection = await InitPolicy.ExecuteAsync(async () =>
                    await ConnectionMultiplexer.ConnectAsync(_options.ConnectionString));
                _redisDb = connection.GetDatabase(_options.CacheDbNumber);
                _connected = true;
            }
            catch (Exception e)
            {
                _logger.ErrorWithObject(e, "Ошибка подключения к редису в фоновой задаче.");
                throw;
            }
        }

        private Dictionary<string, string> GetTracingInfo(string key) =>
            TraceHelper.GetRedisTracingInfo(_options.ConnectionString, key);

        public async Task PublishAsync(string channel, string message)
        {
            if (!_connected)
                return;

            await _redisDb
                .Multiplexer
                .GetSubscriber()
                .PublishAsync(channel, message);
        }

        public async Task<bool> TrySubscribeAsync(string channel, Action<RedisChannel, RedisValue> callback)
        {
            if (!_connected)
                return false;

            await _redisDb
                .Multiplexer
                .GetSubscriber()
                .SubscribeAsync(channel, callback);

            return true;
        }

        private async Task<OperationResult> ExecuteAsync(Func<Task> func)
        {
            if (!_connected)
                return new OperationResult(ActionStatus.InternalOptionalServerUnavailable);

            try
            {
                if (_circuitBreakerPolicy.CircuitState == CircuitState.Open)
                {
                    return new OperationResult(ActionStatus.InternalOptionalServerUnavailable);
                }

                await _policy.ExecuteAsync(func);

                return new OperationResult(ActionStatus.Ok);
            }
            catch (TimeoutRejectedException ex)
            {
                _logger.ErrorWithObject(ex,
                    new {DelegateName = func?.Method.Name, ReturnType = func?.Method.ReturnType.Name});
                return new OperationResult(ActionStatus.Timeout);
            }
            catch (Exception ex)
            {
                _logger.ErrorWithObject(ex,
                    new {DelegateName = func?.Method.Name, ReturnType = func?.Method.ReturnType.Name});
                return new OperationResult(ActionStatus.InternalOptionalServerUnavailable);
            }
        }

        private async Task<OperationResult<T>> ExecuteAsync<T>(Func<Task<T>> func)
        {
            if (!_connected)
                return new OperationResult<T>(ActionStatus.InternalOptionalServerUnavailable);

            try
            {
                if (_circuitBreakerPolicy.CircuitState == CircuitState.Open)
                {
                    return new OperationResult<T>(ActionStatus.InternalOptionalServerUnavailable);
                }

                var result = await _policy.ExecuteAsync(func);

                return result == null || result is RedisValue resultAsRedisValue && resultAsRedisValue.IsNull
                    ? new OperationResult<T>(ActionStatus.NotFound)
                    : new OperationResult<T>(result);
            }
            catch (TimeoutRejectedException ex)
            {
                _logger.ErrorWithObject(ex,
                    new {DelegateName = func?.Method.Name, ReturnType = func?.Method.ReturnType.Name});
                return new OperationResult<T>(ActionStatus.Timeout);
            }
            catch (Exception ex)
            {
                _logger.ErrorWithObject(ex,
                    new {DelegateName = func?.Method.Name, ReturnType = func?.Method.ReturnType.Name});
                return new OperationResult<T>(ActionStatus.InternalOptionalServerUnavailable);
            }
        }

        public async Task<OperationResult> InsertAsync(ICacheEntity redisValue, string metricEntity,
            TimeSpan? longRequestTime = null)
            =>
                await InsertAsync(redisValue, redisValue.GetKey(), _options.TimeToLive, metricEntity, longRequestTime);

        public async Task<OperationResult> InsertAsync<T>(T redisValue, string key, string metricEntity,
            TimeSpan? longRequestTime = null)
            =>
                await InsertAsync(redisValue, key, _options.TimeToLive, metricEntity, longRequestTime);

        public async Task<OperationResult<bool>> InsertIfNotExistsAsync<T>(T redisValue, string key,
            string metricEntity, TimeSpan? longRequestTime = null)
            =>
                await InsertAsync(redisValue, key, _options.TimeToLive, metricEntity, longRequestTime, When.NotExists);

        public async Task<OperationResult<bool>> InsertIfNotExistsAsync(ICacheEntity redisValue, string metricEntity,
            TimeSpan? longRequestTime = null)
            =>
                await InsertAsync(redisValue, redisValue.GetKey(), _options.TimeToLive, metricEntity, longRequestTime,
                    When.NotExists);

        public async Task<OperationResult> InsertManyAsync<T>([NotNull] List<T> redisValues, string metricEntity,
            TimeSpan? longRequestTime = null) where T : ICacheEntity
        {
            if (!_connected)
                return new OperationResult(ActionStatus.InternalOptionalServerUnavailable);
            if (redisValues.Count < 0)
                return new OperationResult(ActionStatus.Ok);

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(
                GetTracingInfo(redisValues.FirstOrDefault()?.GetKey()), metricEntity,
                requestParams: new {RedisValues = redisValues}, longRequestTime: longRequestTime))
            {
                var insertValues =
                    redisValues.Select(value =>
                        new KeyValuePair<RedisKey, RedisValue>(
                            value.GetKey(),
                            JsonConvert.SerializeObject(value, _serializeSettings))).ToArray();
                
                var result = await ExecuteAsync(async () => await _redisDb.StringSetAsync(insertValues));

                return result;
            }
        }

        public async Task InsertManyAsync<T>(Dictionary<string, T> redisValues, string metricEntity,
            TimeSpan? longRequestTime = null)
        {
            if (redisValues == null || redisValues.Count == 0 || !_connected)
                return;

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(
                GetTracingInfo(redisValues.FirstOrDefault().Key), metricEntity,
                requestParams: new {RedisValues = redisValues}, longRequestTime: longRequestTime))
            {
                
                var insertValues =
                    redisValues.Select(value =>
                        new KeyValuePair<RedisKey, RedisValue>(
                            value.Key,
                            JsonConvert.SerializeObject(value.Value, _serializeSettings))).ToArray();
                
                var result = await ExecuteAsync(async () => await _redisDb.StringSetAsync(insertValues));
                
                var tasks =
                    new List<Task>(
                        redisValues.Select(async value => await ExecuteAsync(
                            async () =>
                                await _redisDb.StringSetAsync(value.Key,
                                    JsonConvert.SerializeObject(value.Value, _serializeSettings), _options.TimeToLive))
                        ));
                await Task.WhenAll(tasks);
            }
        }


        public async Task<OperationResult<T>> GetAsync<T>(string key, string metricEntity,
            TimeSpan? longRequestTime = null)
        {
            if (!_connected)
                return new OperationResult<T>(ActionStatus.InternalOptionalServerUnavailable);
            if (string.IsNullOrEmpty(key))
                return new OperationResult<T>();

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(key), metricEntity,
                requestParams: new {RedisKey = key}, longRequestTime: longRequestTime))
            {
                var operationResult = await ExecuteAsync(async () => await _redisDb.StringGetAsync(key));

                if (!operationResult.Success)
                {
                    _counter.Miss();

                    return new OperationResult<T>(operationResult.ActionStatus);
                }

                _counter.Hit();
                var value = JsonConvert.DeserializeObject<T>(operationResult.Value);
                return new OperationResult<T>(value);
            }
        }

        public async Task<OperationResult> DeleteAsync(string key, string metricEntity,
            TimeSpan? longRequestTime = null)
        {
            if (!_connected)
                return new OperationResult(ActionStatus.InternalOptionalServerUnavailable);
            if (string.IsNullOrEmpty(key))
                return new OperationResult(ActionStatus.Ok);

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(key), metricEntity,
                requestParams: new {RedisKey = key}, longRequestTime: longRequestTime))
            {
                return await ExecuteAsync(async () => await _redisDb.KeyDeleteAsync(key));
            }
        }

        public async Task<OperationResult> DeleteAsync(List<string> keys, string metricEntity,
            TimeSpan? longRequestTime = null)
        {
            if (!_connected)
                return new OperationResult(ActionStatus.InternalOptionalServerUnavailable);
            if (keys == null || keys.Count == 0)
                return new OperationResult(ActionStatus.Ok);

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(keys.FirstOrDefault()),
                metricEntity, requestParams: new {RedisKeys = keys}, longRequestTime: longRequestTime))
            {
                return await ExecuteAsync(async () =>
                {
                    foreach (var key in keys)
                    {
                        await _redisDb.KeyDeleteAsync(key);
                    }
                });
            }
        }

        public async Task<OperationResult<List<T>>> GetManyAsync<T>(List<string> keys, string metricEntity,
            bool withNulls = false, TimeSpan? longRequestTime = null)
        {
            if (!_connected)
                return new OperationResult<List<T>>(ActionStatus.InternalOptionalServerUnavailable);
            if (keys == null || keys.Count == 0)
                return new OperationResult<List<T>>();

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(keys.FirstOrDefault()),
                metricEntity, requestParams: new {RedisKeys = keys, WithNulls = withNulls},
                longRequestTime: longRequestTime))
            {
                var keysArray = keys.Select(key => (RedisKey) key).ToArray();
                var operationResult = await ExecuteAsync(async () => await _redisDb.StringGetAsync(keysArray));
                if (!operationResult.Success)
                    return new OperationResult<List<T>>(operationResult);

                var result = withNulls
                    ? operationResult.Value
                        .Select(value => value.HasValue ? JsonConvert.DeserializeObject<T>(value) : default).ToList()
                    : operationResult.Value.Where(value => value.HasValue)
                        .Select(value => JsonConvert.DeserializeObject<T>(value)).ToList();

                var amountOfFoundValues = operationResult.Value.Count(value => value.HasValue);
                _counter.Hit(amountOfFoundValues);
                _counter.Miss(keysArray.Length - amountOfFoundValues);

                return new OperationResult<List<T>>(result);
            }
        }

        public async Task<OperationResult<List<string>>> GetSetAsync(string key, string metricEntity,
            TimeSpan? longRequestTime = null)
        {
            if (!_connected)
                return new OperationResult<List<string>>(ActionStatus.InternalOptionalServerUnavailable);
            if (string.IsNullOrEmpty(key))
                return new OperationResult<List<string>>();

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(key), metricEntity,
                requestParams: new {Key = key}, longRequestTime: longRequestTime))
            {
                var operationResult = await ExecuteAsync(async () => await _redisDb.SetMembersAsync(key));

                if (operationResult.Success)
                {
                    if (operationResult.Value.Length == 0)
                    {
                        return new OperationResult<List<string>>(ActionStatus.NotFound);
                    }

                    if (operationResult.Value.Length == 1 &&
                        string.IsNullOrEmpty(operationResult.Value.FirstOrDefault()))
                    {
                        return new OperationResult<List<string>>(new List<string>());
                    }

                    var result = operationResult.Value.Where(value => value.HasValue && !string.IsNullOrEmpty(value))
                        .Select(value => value.ToString()).ToList();

                    return new OperationResult<List<string>>(result);
                }

                return new OperationResult<List<string>>(operationResult.ActionStatus);
            }
        }

        public async Task<OperationResult<bool>> KeyExistsAsync(string key, string metricEntity,
            TimeSpan? longRequestTime = null)
        {
            if (!_connected)
                return new OperationResult<bool>(ActionStatus.InternalOptionalServerUnavailable);
            if (string.IsNullOrEmpty(key))
                return new OperationResult<bool>();

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(key), metricEntity,
                requestParams: new {Key = key}, longRequestTime: longRequestTime))
            {
                return await ExecuteAsync(async () => await _redisDb.KeyExistsAsync(key));
            }
        }

        public async Task<OperationResult> InsertIntoSetAsync(string setKey, string value, string metricEntity,
            TimeSpan? longRequestTime = null)
        {
            if (!_connected)
                return new OperationResult(ActionStatus.InternalOptionalServerUnavailable);
            if (string.IsNullOrEmpty(setKey))
                return new OperationResult(ActionStatus.Ok);

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(setKey), metricEntity,
                requestParams: new {SetKey = setKey, Value = value}, longRequestTime: longRequestTime))
            {
                return await ExecuteAsync(async () => await _redisDb.SetAddAsync(setKey, value));
            }
        }

        public async Task<OperationResult> InsertIntoSetsAsync(ICollection<string> setKeys, string value,
            string metricEntity, TimeSpan? longRequestTime = null)
        {
            if (!_connected)
                return new OperationResult(ActionStatus.InternalOptionalServerUnavailable);
            if (setKeys == null || setKeys.Count == 0)
                return new OperationResult(ActionStatus.Ok);

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(setKeys.FirstOrDefault()),
                metricEntity, requestParams: new {SetsKeys = setKeys, Value = value}, longRequestTime: longRequestTime))
            {
                var setAddTasks = setKeys.Select(async setKey => await InsertEntityToSetWithPolicy(setKey, value));
                return await ExecuteAsync(async () => await Task.WhenAll(setAddTasks));
            }

            async Task<OperationResult<bool>> InsertEntityToSetWithPolicy(string key, string val) =>
                await ExecuteAsync(async () => await _redisDb.SetAddAsync(key, val));
        }

        public async Task<OperationResult<List<string>>> GetManySetsAsync(List<string> keys, string metricEntity,
            bool withNulls = false, TimeSpan? longRequestTime = null)
        {
            if (!_connected)
                return new OperationResult<List<string>>(ActionStatus.InternalOptionalServerUnavailable);
            if (keys == null || keys.Count == 0)
                return new OperationResult<List<string>>();

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(keys.FirstOrDefault()),
                metricEntity, requestParams: new {RedisKeys = keys, WithNulls = withNulls},
                longRequestTime: longRequestTime))
            {
                var transaction = _redisDb.CreateTransaction();
                var operations = keys.Select(key =>
                    new Func<ITransaction, Task<RedisValue[]>>(tran => tran.SetMembersAsync(key)));
                var responses = operations.Select(command => command(transaction)).ToList();

                var operationResult = await ExecuteAsync(async () => await transaction.ExecuteAsync());
                if (!operationResult.Success)
                    return new OperationResult<List<string>>(operationResult);

                var results = new List<string>();
                foreach (var response in responses)
                {
                    var result = withNulls
                        ? response.Result.Select(value => value.HasValue ? value.ToString() : default).ToList()
                        : response.Result.Where(value => value.HasValue).Select(value => value.ToString()).ToList();

                    results.AddRange(result);
                }

                return new OperationResult<List<string>>(results);
            }
        }

        public async Task<OperationResult> InsertMultiManyAndSetAsync<T>(List<T> manyRedisValues, string setKey,
            string metricEntity, TimeSpan? longRequestTime = null) where T : ICacheEntity
        {
            if (!_connected)
                return new OperationResult(ActionStatus.InternalOptionalServerUnavailable);
            if (string.IsNullOrEmpty(setKey))
                return new OperationResult(ActionStatus.Ok);

            if (manyRedisValues.Count == 0)
                return await ExecuteAsync(async () => await _redisDb.SetAddAsync(setKey, ""));

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(setKey), metricEntity,
                requestParams: new {RedisValues = manyRedisValues, SetKey = setKey}, longRequestTime: longRequestTime))
            {
                var transaction = _redisDb.CreateTransaction();
                foreach (var redisValue in manyRedisValues)
                {
                    transaction.StringSetAsync(redisValue.GetKey(),
                        JsonConvert.SerializeObject(redisValue, _serializeSettings),
                        _options.TimeToLive).Forget();
                }

                transaction.SetAddAsync(setKey, manyRedisValues.Select(value => (RedisValue) value.GetKey()).ToArray())
                    .Forget();
                return await ExecuteAsync(async () => await transaction.ExecuteAsync());
            }
        }


        public async Task<OperationResult> IncrementAsync(string key, DateTime expireAt, string metricEntity,
            TimeSpan? longTimeRequest = null)
        {
            if (!_connected)
                return new OperationResult(ActionStatus.InternalOptionalServerUnavailable);
            if (string.IsNullOrEmpty(key))
                return new OperationResult(ActionStatus.Ok);

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(key), metricEntity,
                requestParams: new {Key = key, ExpireAt = expireAt}, longRequestTime: longTimeRequest))
            {
                var transaction = _redisDb.CreateTransaction();
                transaction.StringIncrementAsync(key).Forget();
                transaction.KeyExpireAsync(key, expireAt).Forget();
                return await ExecuteAsync(async () => await transaction.ExecuteAsync());
            }
        }

        public async Task<OperationResult> DeleteFromSetAsync(string setKey, string member, string metricEntity,
            TimeSpan? longTimeRequest = null)
        {
            if (!_connected)
                return new OperationResult(ActionStatus.InternalOptionalServerUnavailable);
            if (string.IsNullOrEmpty(setKey))
                return new OperationResult(ActionStatus.Ok);

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(setKey), metricEntity,
                requestParams: new {SetKey = setKey, MemberOfSet = member}, longRequestTime: longTimeRequest))
            {
                return await ExecuteAsync(async () => await _redisDb.SetRemoveAsync(setKey, member));
            }
        }

        public async Task<OperationResult<bool>> IsMemberOfSetAsync(string setKey, string member, string metricEntity,
            TimeSpan? longTimeRequest = null)
        {
            if (!_connected)
                return new OperationResult<bool>(ActionStatus.InternalOptionalServerUnavailable);
            if (string.IsNullOrEmpty(setKey))
                return new OperationResult<bool>();

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(setKey), metricEntity,
                requestParams: new {SetKey = setKey, MemberOfSet = member}, longRequestTime: longTimeRequest))
            {
                return await ExecuteAsync(async () => await _redisDb.SetContainsAsync(setKey, member));
            }
        }

        public async Task<OperationResult<RedisValue>> GetFromHashAsync(string hashKey, string hashField,
            string metricEntity, TimeSpan? longTimeRequest = null)
        {
            if (!_connected)
                return new OperationResult<RedisValue>(ActionStatus.InternalOptionalServerUnavailable);
            if (string.IsNullOrEmpty(hashKey) || string.IsNullOrEmpty(hashField))
                return new OperationResult<RedisValue>();

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(hashKey), metricEntity,
                requestParams: new {HashKey = hashKey, HashField = hashField}, longRequestTime: longTimeRequest))
            {
                return await ExecuteAsync(async () => await _redisDb.HashGetAsync(hashKey, hashField));
            }
        }

        public async Task<OperationResult> InsertIntoHashAsync(string hashKey, string hashField, RedisValue value,
            string metricEntity, TimeSpan? longTimeRequest = null)
        {
            if (!_connected)
                return new OperationResult(ActionStatus.InternalOptionalServerUnavailable);
            if (string.IsNullOrEmpty(hashKey) || string.IsNullOrEmpty(hashField))
                return new OperationResult(ActionStatus.Ok);

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(hashKey), metricEntity,
                requestParams: new {HashKey = hashKey, HashField = hashField}, longRequestTime: longTimeRequest))
            {
                return await ExecuteAsync(async () => await _redisDb.HashSetAsync(hashKey, hashField, value));
            }
        }

        public async Task<OperationResult> DeleteFromHashAsync(string hashKey, string hashField, string metricEntity,
            TimeSpan? longTimeRequest = null)
        {
            if (!_connected)
                return new OperationResult(ActionStatus.InternalOptionalServerUnavailable);
            if (string.IsNullOrEmpty(hashKey) || string.IsNullOrEmpty(hashField))
                return new OperationResult(ActionStatus.Ok);

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(hashKey), metricEntity,
                requestParams: new {HashKey = hashKey, HashField = hashField}, longRequestTime: longTimeRequest))
            {
                return await ExecuteAsync(async () => await _redisDb.HashDeleteAsync(hashKey, hashField));
            }
        }

        public async Task<OperationResult> ExpireAsync(string key, DateTime expiration, string metricEntity,
            TimeSpan? longRequestTime = null)
        {
            if (!_connected)
                return new OperationResult(ActionStatus.InternalOptionalServerUnavailable);
            if (string.IsNullOrEmpty(key))
                return new OperationResult(ActionStatus.Ok);

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(key), metricEntity,
                requestParams: new {Key = key, Expiration = expiration}, longRequestTime: longRequestTime))
            {
                return await ExecuteAsync(async () => await _redisDb.KeyExpireAsync(key, expiration.ToUniversalTime()));
            }
        }

        private async Task<OperationResult<bool>> InsertAsync<T>(T redisValue, string key, TimeSpan? timeToLive,
            string metricEntity, TimeSpan? longTimeRequest = null, When when = When.Always)
        {
            if (!_connected)
                return new OperationResult<bool>(ActionStatus.InternalOptionalServerUnavailable);
            if (string.IsNullOrEmpty(key))
                return new OperationResult<bool>();

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(key), metricEntity,
                requestParams: new {Value = redisValue, Key = key, TimeToLive = timeToLive},
                longRequestTime: longTimeRequest))
            {
                return await ExecuteAsync(async () => await _redisDb.StringSetAsync(key,
                    JsonConvert.SerializeObject(redisValue, _serializeSettings), timeToLive, when));
            }
        }

        //TODO: Проверить работу через хеши. При больших объемах работает быстрее, чем сеты, но существует вопрос при вставке одиночной записи
        public async Task<OperationResult> InsertHashAsync<T>(List<T> manyRedisValues, string hashKey,
            string metricEntity, TimeSpan? longTimeRequest = null) where T : ICacheEntity
        {
            if (!_connected)
                return new OperationResult(ActionStatus.InternalOptionalServerUnavailable);
            if (string.IsNullOrEmpty(hashKey))
                return new OperationResult(ActionStatus.Ok);

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(hashKey), metricEntity,
                requestParams: new {RedisValues = manyRedisValues, HashKey = hashKey},
                longRequestTime: longTimeRequest))
            {
                return await ExecuteAsync(async () => await _redisDb.HashSetAsync(hashKey,
                    manyRedisValues
                        .Select(value =>
                            new HashEntry(value.GetKey(), JsonConvert.SerializeObject(value, _serializeSettings)))
                        .ToArray()));
            }
        }

        public async Task<OperationResult<T>> GetFromHashAsync<T>(string hashKey, string hashField, string metricEntity,
            TimeSpan? longTimeRequest = null)
        {
            if (!_connected)
                return new OperationResult<T>(ActionStatus.InternalOptionalServerUnavailable);
            if (string.IsNullOrEmpty(hashKey) || string.IsNullOrEmpty(hashField))
                return new OperationResult<T>();

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(hashKey), metricEntity,
                requestParams: new {HashKey = hashKey, HashField = hashField}, longRequestTime: longTimeRequest))
            {
                var operationResult = await ExecuteAsync(async () => await _redisDb.HashGetAsync(hashKey, hashField));

                if (!operationResult.Success)
                {
                    _counter.Miss();

                    return new OperationResult<T>(operationResult.ActionStatus);
                }

                _counter.Hit();
                var value = JsonConvert.DeserializeObject<T>(operationResult.Value);
                return new OperationResult<T>(value);
            }
        }

        public async Task<OperationResult<List<T>>> GetManyFromHashAsync<T>(string hashKey, string metricEntity,
            TimeSpan? longTimeRequest = null)
        {
            if (!_connected)
                return new OperationResult<List<T>>(ActionStatus.InternalOptionalServerUnavailable);
            if (string.IsNullOrEmpty(hashKey))
                return new OperationResult<List<T>>();

            using (_metricsTracingFactory.CreateTracingWithLoggingMetricsTimer(GetTracingInfo(hashKey), metricEntity,
                requestParams: new {HashKey = hashKey}, longRequestTime: longTimeRequest))
            {
                var operationResult = await ExecuteAsync(async () => await _redisDb.HashValuesAsync(hashKey));
                if (operationResult.Success)
                {
                    if (operationResult.Value.Length == 0)
                    {
                        return new OperationResult<List<T>>(ActionStatus.NotFound);
                    }

                    var result = operationResult.Value.Where(value => value.HasValue)
                        .Select(value => JsonConvert.DeserializeObject<T>(value)).ToList();

                    var amountOfFoundValues = operationResult.Value.Count(value => value.HasValue);
                    _counter.Hit(amountOfFoundValues);

                    return new OperationResult<List<T>>(result);
                }

                return new OperationResult<List<T>>(operationResult.ActionStatus);
            }
        }
    }
}