﻿#if DOTNET5_4
using Discord.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Globalization;

namespace Discord.Net.Rest
{
    internal sealed class BuiltInEngine : IRestEngine
    {
        private const int HR_SECURECHANNELFAILED = -2146233079;

        private readonly DiscordConfig _config;
		private readonly HttpClient _client;
        private readonly string _baseUrl;

        private readonly object _rateLimitLock;
        private DateTime _rateLimitTime;

        internal Logger Logger { get; }

        public BuiltInEngine(DiscordConfig config, string baseUrl, Logger logger)
		{
			_config = config;
            _baseUrl = baseUrl;
            Logger = logger;

            _rateLimitLock = new object();
            _client = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip,
                UseCookies = false,
                UseProxy = false,
                PreAuthenticate = false //We do auth ourselves
            });
            _client.DefaultRequestHeaders.Add("accept", "*/*");
            _client.DefaultRequestHeaders.Add("accept-encoding", "gzip,deflate");
            _client.DefaultRequestHeaders.Add("user-agent", config.UserAgent);
        }

		public void SetToken(string token)
		{
			_client.DefaultRequestHeaders.Remove("authorization");
			if (token != null)
				_client.DefaultRequestHeaders.Add("authorization", token);
		}

		public async Task<string> Send(string method, string path, string json, CancellationToken cancelToken)
		{
            using (var request = new HttpRequestMessage(GetMethod(method), _baseUrl + path))
            {
                if (json != null)
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return await Send(request, cancelToken);
            }
        }
		public async Task<string> SendFile(string method, string path, string filename, Stream stream, CancellationToken cancelToken)
		{
            using (var request = new HttpRequestMessage(GetMethod(method), _baseUrl + path))
            {
                var content = new MultipartFormDataContent("Upload----" + DateTime.Now.ToString(CultureInfo.InvariantCulture));
                content.Add(new StreamContent(File.OpenRead(path)), "file", filename);
                request.Content = content;
                return await Send(request, cancelToken);
            }
        }
		private async Task<string> Send(HttpRequestMessage request, CancellationToken cancelToken)
        {
            int retryCount = 0;
            while (true)
            {
                HttpResponseMessage response;
                try
                {
                    response = await _client.SendAsync(request, cancelToken).ConfigureAwait(false);
                }
                catch (WebException ex)
                {
                    //The request was aborted: Could not create SSL/TLS secure channel.
                    if (ex.HResult == HR_SECURECHANNELFAILED && retryCount++ < 5)
                        continue; //Retrying seems to fix this somehow?
                    throw;
                }

                int statusCode = (int)response.StatusCode;
                if (statusCode == 429) //Rate limit
                {
                    var retryAfter = response.Headers
                        .Where(x => x.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.Value.FirstOrDefault())
                        .FirstOrDefault();

                    int milliseconds;
                    if (retryAfter != null && int.TryParse(retryAfter, out milliseconds))
                    {
                        var now = DateTime.UtcNow;
                        if (now >= _rateLimitTime)
                        {
                            lock (_rateLimitLock)
                            {
                                if (now >= _rateLimitTime)
                                {
                                    _rateLimitTime = now.AddMilliseconds(milliseconds);
                                    Logger.Warning($"Rate limit hit, waiting {Math.Round(milliseconds / 1000.0f, 2)} seconds");
                                }
                            }
                        }
                        await Task.Delay(milliseconds, cancelToken).ConfigureAwait(false);
                        continue;
                    }
                    throw new HttpException(response.StatusCode);
                }
                else if (statusCode < 200 || statusCode >= 300) //2xx = Success
                    throw new HttpException(response.StatusCode);
                else
                    return await response.Content.ReadAsStringAsync();
            }
		}

        private static readonly HttpMethod _patch = new HttpMethod("PATCH");
		private HttpMethod GetMethod(string method)
		{
			switch (method)
			{
				case "DELETE": return HttpMethod.Delete;
				case "GET": return HttpMethod.Get;
				case "PATCH": return _patch;
				case "POST": return HttpMethod.Post;
				case "PUT": return HttpMethod.Put;
				default: throw new InvalidOperationException($"Unknown HttpMethod: {method}");
			}
		}
	}
}
#endif