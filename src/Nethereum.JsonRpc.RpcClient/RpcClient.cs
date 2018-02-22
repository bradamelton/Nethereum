
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethereum.JsonRpc.Client.RpcMessages;
using Newtonsoft.Json.Linq;

namespace Nethereum.JsonRpc.Client
{
    public class RpcClient : ClientBase
    {
        private readonly Uri _baseUrl;
        private readonly AuthenticationHeaderValue _authHeaderValue;
        private readonly JsonSerializerSettings _jsonSerializerSettings;
        private readonly HttpClientHandler _httpClientHandler;
        private DateTime _httpClientLastCreatedAt;
        private HttpClient _httpClient;
        private HttpClient _httpClient2;
        private object _lockObject = new object();
        private volatile bool _firstHttpClient = false;
        private const int NUMBER_OF_SECONDS_TO_RECREATE_HTTP_CLIENT = 60;

        public RpcClient(Uri baseUrl, AuthenticationHeaderValue authHeaderValue = null,
            JsonSerializerSettings jsonSerializerSettings = null, HttpClientHandler httpClientHandler = null)
        {
            _baseUrl = baseUrl;
            _authHeaderValue = authHeaderValue;
            if (jsonSerializerSettings == null)
            {
                jsonSerializerSettings = DefaultJsonSerializerSettingsFactory.BuildDefaultJsonSerializerSettings();
            }
            _jsonSerializerSettings = jsonSerializerSettings;
            _httpClientHandler = httpClientHandler;
            CreateNewHttpClient();

        }

        protected override async Task<T> SendInnerRequestAync<T>(RpcRequest request, string route = null)
        {
            var response =
                await SendAsync(
                        new RpcRequestMessage(request.Id, request.Method, (object[])request.RawParameters), route)
                    .ConfigureAwait(false);
            HandleRpcError(response);
            return response.GetResult<T>();
        }

        protected override async Task<T> SendInnerRequestAync<T>(string method, string route = null,
            params object[] paramList)
        {
            var request = new RpcRequestMessage(Guid.NewGuid().ToString(), method, paramList);
            var response = await SendAsync(request, route).ConfigureAwait(false);
            HandleRpcError(response);
            return response.GetResult<T>();
        }

        private void HandleRpcError(RpcResponseMessage response)
        {
            if (response.HasError)
            {
                // Geth reporting unknown transaction error. Should not be an exception.
                if (response.Error.Code != -32000)
                {
                    throw new RpcResponseException(new RpcError(response.Error.Code, response.Error.Message,
                        response.Error.Data));
                }
            }
        }

        public override async Task SendRequestAsync(RpcRequest request, string route = null)
        {
            var response =
                await SendAsync(
                        new RpcRequestMessage(request.Id, request.Method, (object[])request.RawParameters), route)
                    .ConfigureAwait(false);
            HandleRpcError(response);
        }

        public override async Task SendRequestAsync(string method, string route = null, params object[] paramList)
        {
            var request = new RpcRequestMessage(Guid.NewGuid().ToString(), method, paramList);
            var response = await SendAsync(request, route).ConfigureAwait(false);
            HandleRpcError(response);
        }

        private async Task<RpcResponseMessage> SendAsync(RpcRequestMessage request, string route = null)
        {
            try
            {
                var httpClient = GetOrCreateHttpClient();
                var rpcRequestJson = JsonConvert.SerializeObject(request, _jsonSerializerSettings);
                var httpContent = new StringContent(rpcRequestJson, Encoding.UTF8, "application/json");
                var httpResponseMessage = await httpClient.PostAsync(route, httpContent).ConfigureAwait(false);
                httpResponseMessage.EnsureSuccessStatusCode();

                var stream = await httpResponseMessage.Content.ReadAsStreamAsync();
                using (var streamReader = new StreamReader(stream))
                using (var reader = new JsonTextReader(streamReader))
                {
                    var serializer = JsonSerializer.Create(_jsonSerializerSettings);
                    return serializer.Deserialize<RpcResponseMessage>(reader);
                }

            }
            catch (Exception ex)
            {
                throw new RpcClientUnknownException("Error occurred when trying to send rpc requests(s)", ex);
            }

        }

        private HttpClient GetOrCreateHttpClient()
        {
            lock (_lockObject)
            {
                var timeSinceCreated = DateTime.UtcNow - _httpClientLastCreatedAt;
                if (timeSinceCreated.TotalSeconds > NUMBER_OF_SECONDS_TO_RECREATE_HTTP_CLIENT)
                {
                    CreateNewHttpClient();
                }
                return GetClient();
            }
        }

        private HttpClient GetClient()
        {
            lock (_lockObject)
            {
                return _firstHttpClient ? _httpClient : _httpClient2;
            }
        }

        private void CreateNewHttpClient()
        {
            var httpClient = _httpClientHandler != null ? new HttpClient(_httpClientHandler) : new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = _authHeaderValue;
            httpClient.BaseAddress = _baseUrl;
            _httpClientLastCreatedAt = DateTime.UtcNow;
            if (_firstHttpClient)
            {
                lock (_lockObject)
                {
                    _firstHttpClient = false;
                    _httpClient2 = httpClient;
                }
            }
            else
            {
                lock (_lockObject)
                {
                    _firstHttpClient = true;
                    _httpClient = httpClient;
                }
            }
        }
    }
}