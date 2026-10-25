using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;

#pragma warning disable SYSLIB0014 // HttpWebRequest 在 net10 被标 obsolete，但它是 net472 上唯一稳的路子（同一份源码）

namespace UI18n.Runtime.Update
{
    /// <summary>
    /// 极小的 HTTP GET 客户端，带**硬超时**。
    /// <para>
    /// 为什么不用 <c>HttpClient</c>：net472 目标里它要拉 System.Net.Http 包（且 KSP 的 Mono 里没有），
    /// 而本项目零第三方依赖。<c>HttpWebRequest</c> 在 net472 是原生类型，net10 也仍然可用（只是标了 obsolete）。
    /// </para>
    /// <para>
    /// 硬超时：<c>HttpWebRequest.Timeout</c> 只管「建立连接 + 收响应头」，
    /// 读响应体时可能无限期挂着（半开连接）。所以这里把整个请求丢到后台线程，
    /// 主调方 <c>Join</c> 一个超时，到点就 <c>Abort()</c> 真正把 socket 收掉。
    /// </para>
    /// <para>任何失败都返回带 <see cref="Response.Error"/> 的对象，**绝不抛异常**。</para>
    /// </summary>
    internal static class UpdateHttp
    {
        /// <summary>响应体上限（32 MB）：manifest 20 KB、zip 1 MB，这个量级足够且能防被灌爆内存。</summary>
        internal const int MaxBodyBytes = 32 * 1024 * 1024;

        internal sealed class Response
        {
            internal bool Ok;
            internal int StatusCode;
            internal byte[] Body;
            internal string Error;

            internal string BodyText
            {
                get
                {
                    if (Body == null)
                    {
                        return string.Empty;
                    }

                    return new System.Text.UTF8Encoding(false).GetString(Body);
                }
            }
        }

        private sealed class FetchState
        {
            internal HttpWebRequest Request;
            internal Response Response;
        }

        /// <summary>发一个 GET，返回响应或错误（永不抛）。</summary>
        internal static Response Get(string url, int timeoutSeconds)
        {
            Response response = new Response();
            if (string.IsNullOrEmpty(url))
            {
                response.Error = "empty-url";
                return response;
            }

            int seconds = timeoutSeconds > 0 ? timeoutSeconds : 10;
            int milliseconds = seconds * 1000;

            FetchState state = new FetchState();
            Thread worker = new Thread(delegate()
            {
                try
                {
                    state.Response = Fetch(url, milliseconds, state);
                }
                catch (Exception ex)
                {
                    state.Response = Failure(ex.GetType().Name + ": " + ex.Message);
                }
            });
            worker.IsBackground = true;
            worker.Name = "UI18n.Update.Http";

            try
            {
                worker.Start();
            }
            catch (Exception ex)
            {
                response.Error = "thread-start-failed: " + ex.GetType().Name + ": " + ex.Message;
                return response;
            }

            if (!worker.Join(milliseconds))
            {
                // 硬超时：线程可能卡在 DNS / TLS 握手里，Abort 才能把 socket 收掉
                HttpWebRequest pending = state.Request;
                if (pending != null)
                {
                    try
                    {
                        pending.Abort();
                    }
                    catch
                    {
                    }
                }

                response.Error = "timeout after " + seconds.ToString(CultureInfo.InvariantCulture) + "s";
                return response;
            }

            return state.Response ?? Failure("empty-response");
        }

        private static Response Failure(string error)
        {
            Response response = new Response();
            response.Error = error;
            return response;
        }

        private static Response Fetch(string url, int milliseconds, FetchState state)
        {
#if NET472
            // macOS / 老 Windows 上的 Mono 默认安全协议可能还是 TLS 1.0，公网 https 会直接握手失败
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch
            {
            }
#endif

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                return Failure("invalid-url: " + url);
            }

            HttpWebRequest request = WebRequest.Create(uri) as HttpWebRequest;
            if (request == null)
            {
                return Failure("not-http-url: " + url);
            }

            state.Request = request;
            request.Method = "GET";
            request.Timeout = milliseconds;
            request.ReadWriteTimeout = milliseconds;
            request.AllowAutoRedirect = true;
            request.MaximumAutomaticRedirections = 5;
            request.UserAgent = "UI18n-Updater/1.0 (+ksp)";

            HttpWebResponse web;
            try
            {
                web = (HttpWebResponse)request.GetResponse();
            }
            catch (WebException wex)
            {
                // 4xx/5xx 在这里以异常形式回来：普通 HTTP 状态码不该让日志里出现英文异常原句
                HttpWebResponse errorResponse = wex.Response as HttpWebResponse;
                if (errorResponse != null)
                {
                    int code = (int)errorResponse.StatusCode;
                    try
                    {
                        errorResponse.Close();
                    }
                    catch
                    {
                    }

                    Response statusFailure = new Response();
                    statusFailure.StatusCode = code;
                    statusFailure.Error = "HTTP " + code.ToString(CultureInfo.InvariantCulture);
                    return statusFailure;
                }

                return Failure("network-error: " + wex.Status + ": " + wex.Message);
            }

            using (web)
            {
                Response response = new Response();
                response.StatusCode = (int)web.StatusCode;

                using (Stream stream = web.GetResponseStream())
                {
                    if (stream == null)
                    {
                        return Failure("empty-stream");
                    }

                    using (MemoryStream buffer = new MemoryStream())
                    {
                        byte[] chunk = new byte[16 * 1024];
                        int read;
                        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                        {
                            if (buffer.Length + read > MaxBodyBytes)
                            {
                                return Failure("body-too-large (>" + MaxBodyBytes.ToString(CultureInfo.InvariantCulture) + " B)");
                            }

                            buffer.Write(chunk, 0, read);
                        }

                        response.Body = buffer.ToArray();
                    }
                }

                response.Ok = true;
                return response;
            }
        }
    }
}

#pragma warning restore SYSLIB0014
