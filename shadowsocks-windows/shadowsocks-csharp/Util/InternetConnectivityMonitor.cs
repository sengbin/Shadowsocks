using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Util
{
    /// <summary>
    /// 联网状态变化事件参数。
    /// </summary>
    public class ConnectivityChangedEventArgs : EventArgs
    {
        /// <summary>
        /// 初始化事件参数实例。
        /// </summary>
        /// <param name="isConnected">当前联网状态。</param>
        public ConnectivityChangedEventArgs(bool isConnected)
        {
            IsConnected = isConnected;
        }

        /// <summary>
        /// 当前是否已连接互联网。
        /// </summary>
        public bool IsConnected { get; }
    }

    /// <summary>
    /// 提供异步联网状态监控能力。
    /// </summary>
    public sealed class InternetConnectivityMonitor : IDisposable
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly TimeSpan _checkInterval;
        private bool _lastStatus = true;
        // 连续失败/成功计数和平滑阈值，避免短暂网络抖动导致误报
        private int _consecutiveFailures = 0;
        private int _consecutiveSuccesses = 0;
        private readonly int _failureThreshold;
        private readonly int _successThreshold;
        private readonly Uri _probeUri = new Uri("https://www.baidu.com");

        /// <summary>
        /// 联网状态变化事件。
        /// </summary>
        public event EventHandler<ConnectivityChangedEventArgs> ConnectivityChanged;

        /// <summary>
        /// 初始化监控器。
        /// </summary>
        /// <param name="checkInterval">自定义检测周期，默认为 5 秒。</param>
        /// <summary>
        /// 初始化监控器。
        /// </summary>
        /// <param name="checkInterval">检测间隔，默认 5 秒。</param>
        /// <param name="successThreshold">判定为在线前需要的连续成功次数，默认 2。</param>
        /// <param name="failureThreshold">判定为离线前需要的连续失败次数，默认 3。</param>
        public InternetConnectivityMonitor(TimeSpan? checkInterval = null, int successThreshold = 1, int failureThreshold = 1)
        {
            _checkInterval = checkInterval ?? TimeSpan.FromSeconds(5);
            _successThreshold = Math.Max(1, successThreshold);
            _failureThreshold = Math.Max(1, failureThreshold);
        }

        /// <summary>
        /// 启动异步检测循环。
        /// </summary>
        public void Start()
        {
            Task.Run(MonitorLoopAsync);
        }

        /// <summary>
        /// 释放内部资源。
        /// </summary>
        public void Dispose()
        {
            if (!_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
            }

            _cancellationTokenSource.Dispose();
        }

        /// <summary>
        /// 异步执行检测循环。
        /// </summary>
        private async Task MonitorLoopAsync()
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                bool probeOk = false;
                try
                {
                    probeOk = await CheckConnectivityAsync().ConfigureAwait(false);
                }
                catch
                {
                    probeOk = false;
                }

                if (probeOk)
                {
                    _consecutiveSuccesses++;
                    _consecutiveFailures = 0;
                    if (_consecutiveSuccesses >= _successThreshold && _lastStatus == false)
                    {
                        _lastStatus = true;
                        ConnectivityChanged?.Invoke(this, new ConnectivityChangedEventArgs(true));
                    }
                }
                else
                {
                    _consecutiveFailures++;
                    _consecutiveSuccesses = 0;
                    if (_consecutiveFailures >= _failureThreshold && _lastStatus == true)
                    {
                        _lastStatus = false;
                        ConnectivityChanged?.Invoke(this, new ConnectivityChangedEventArgs(false));
                    }
                }

                try
                {
                    await Task.Delay(_checkInterval, _cancellationTokenSource.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// 检查当前是否能够访问互联网。
        /// </summary>
        private async Task<bool> CheckConnectivityAsync()
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                return false;
            }
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(_probeUri);
                request.Method = "GET";
                // 允许使用系统代理，以便在需要代理的网络环境中也能正确探测
                // request.Proxy = null;
                // 设置读写超时作为备用（对同步 API 有效），对异步我们使用显式的 Task.WhenAny 超时判断
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;
                request.KeepAlive = false;
                request.UserAgent = "ShadowsocksConnectivityMonitor";

                // 如果 GetResponseAsync 在 5 秒内没有完成，则视为超时并判断为断网
                var getResponseTask = request.GetResponseAsync();
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
                var finished = await Task.WhenAny(getResponseTask, timeoutTask).ConfigureAwait(false);
                if (finished != getResponseTask)
                {
                    try { request.Abort(); } catch { }
                    return false;
                }

                using (HttpWebResponse response = (HttpWebResponse)await getResponseTask.ConfigureAwait(false))
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
