using System;
using System.Diagnostics;
using ATI.Services.Common.Logging;
using NLog;
using Prometheus;

namespace ATI.Services.Common.Metrics
{
    internal class MetricsTimer : IDisposable
    {
        private Summary _summary { get; }
        private Stopwatch _stopwatch { get; }
        private string[] _summaryLabels { get; }
        private readonly TimeSpan? _longRequestTime;
        private readonly object _context;
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly LogSource? _logSource;

        /// <summary>
        /// Конструктор таймера метрик, который считает только метрику (время выполнения + счётчик) для прометеуса
        /// </summary>
        
        public MetricsTimer(
            Summary summary, 
            string[] additionSummaryLabels, 
            TimeSpan? longRequestTime = null, 
            object context = null, 
            LogSource? logSource = null)
        {
            _summary = summary;
            _summaryLabels = additionSummaryLabels;

            _stopwatch = new Stopwatch();
            _stopwatch.Start();
            _longRequestTime = longRequestTime;
            _context = context;
            _logSource = logSource;
        }


        public void Dispose()
        {
            if (_summary != null)
            {
                if (_summaryLabels == null)
                {
                    _summary.Observe(_stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    _summary.Labels(_summaryLabels).Observe(_stopwatch.ElapsedMilliseconds);
                }
                _stopwatch.Stop();

                if (_longRequestTime != null && _stopwatch.Elapsed > _longRequestTime && _context != null && _logSource != null)
                { 
                    _logger.LogLongRequest(
                        _logSource.Value,
                        new
                        {
                            LogSource = _logSource,
                            RequestTime = _stopwatch.Elapsed,
                            Labels = _summaryLabels,
                            Context = _context
                        });
                }
            }
        }
    }
}
