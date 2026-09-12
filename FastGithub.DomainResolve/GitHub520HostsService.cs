using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 表示GitHub520 hosts源服务
    /// 定时拉取社区维护的GitHub域名解析记录，作为域名解析的优先数据源
    /// 数据源失效时自动回退到内置DNS解析
    /// </summary>
    sealed class GitHub520HostsService : BackgroundService
    {
        // hosts数据源：主源 + 备用源，任一可用即可
        private static readonly string[] hostsUrls = new[]
        {
            "https://raw.hellogithub.com/hosts",
            "https://fastly.jsdelivr.net/gh/521xueweihan/GitHub520@main/hosts",
            "https://cdn.jsdelivr.net/gh/521xueweihan/GitHub520@main/hosts"
        };

        // 独立于FastGithub代理栈的httpClient，避免与DNS解析相互依赖
        private static readonly HttpClient httpClient = CreateHttpClient();

        // hosts缓存：域名 -> IP列表
        private volatile ConcurrentDictionary<string, IPAddress[]> hostsCache = new(StringComparer.OrdinalIgnoreCase);

        // 刷新周期
        private readonly TimeSpan refreshPeriod = TimeSpan.FromMinutes(30d);

        // 拉取失败后的重试周期
        private readonly TimeSpan retryPeriod = TimeSpan.FromSeconds(30d);

        // 单次请求超时
        private readonly TimeSpan requestTimeout = TimeSpan.FromSeconds(10d);

        private readonly ILogger<GitHub520HostsService> logger;

        /// <summary>
        /// GitHub520 hosts源服务
        /// </summary>
        /// <param name="logger"></param>
        public GitHub520HostsService(ILogger<GitHub520HostsService> logger)
        {
            this.logger = logger;
        }

        /// <summary>
        /// 尝试获取hosts源中指定域名的IP列表
        /// </summary>
        /// <param name="host"></param>
        /// <param name="addresses"></param>
        /// <returns>hosts源无该域名或尚未拉取成功时返回false</returns>
        public bool TryGetAddresses(string host, out IPAddress[] addresses)
        {
            return this.hostsCache.TryGetValue(host, out addresses!) && addresses.Length > 0;
        }

        /// <summary>
        /// 后台定时拉取hosts数据
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (stoppingToken.IsCancellationRequested == false)
            {
                var success = await this.RefreshAsync(stoppingToken);
                try
                {
                    await Task.Delay(success ? this.refreshPeriod : this.retryPeriod, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// 拉取并刷新hosts缓存
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns>是否刷新成功</returns>
        private async Task<bool> RefreshAsync(CancellationToken stoppingToken)
        {
            foreach (var url in hostsUrls)
            {
                try
                {
                    using var timeoutTokenSource = new CancellationTokenSource(this.requestTimeout);
                    using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutTokenSource.Token);
                    var content = await httpClient.GetStringAsync(url, linkedTokenSource.Token);
                    var items = ParseHosts(content);
                    if (items.Count > 0)
                    {
                        var cache = new ConcurrentDictionary<string, IPAddress[]>(StringComparer.OrdinalIgnoreCase);
                        foreach (var item in items)
                        {
                            cache[item.Key] = item.Value;
                        }

                        this.hostsCache = cache;
                        this.logger.LogInformation("GitHub520 hosts源更新成功：{Url}，共{Count}条记录", url, cache.Count);
                        return true;
                    }

                    this.logger.LogWarning("GitHub520 hosts源无有效记录：{Url}", url);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    this.logger.LogWarning("GitHub520 hosts源请求超时：{Url}", url);
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning("GitHub520 hosts源拉取失败：{Url} {Message}", url, ex.Message);
                }
            }

            this.logger.LogWarning("GitHub520 hosts源全部不可用，将使用内置DNS解析");
            return false;
        }

        /// <summary>
        /// 解析hosts内容为 域名->IP列表 的映射
        /// </summary>
        /// <param name="content"></param>
        /// <returns></returns>
        private static List<KeyValuePair<string, IPAddress[]>> ParseHosts(string content)
        {
            var groups = new Dictionary<string, List<IPAddress>>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || IPAddress.TryParse(parts[0], out var address) == false || IPAddress.IsLoopback(address))
                {
                    continue;
                }

                var host = parts[1].ToLower();
                if (groups.TryGetValue(host, out var list) == false)
                {
                    list = new List<IPAddress>();
                    groups.Add(host, list);
                }

                if (list.Contains(address) == false)
                {
                    list.Add(address);
                }
            }

            return groups.Select(item => new KeyValuePair<string, IPAddress[]>(item.Key, item.Value.ToArray())).ToList();
        }

        /// <summary>
        /// 创建httpClient
        /// </summary>
        /// <returns></returns>
        private static HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli
            };
            var client = new HttpClient(handler, disposeHandler: false);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FastGithub");
            return client;
        }
    }
}
