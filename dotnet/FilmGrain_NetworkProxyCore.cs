using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Text;

namespace FilmGrainStudioPreview
{
    internal static class NetworkProxyCore
    {
        private static readonly string[] EnvironmentProxyNames = new string[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" };
        private static readonly string[] InitialProxyEnvironment = CaptureInitialProxyEnvironment();
        internal const string SystemMode = "SYSTEM";
        internal const string CustomMode = "CUSTOM";
        internal const string DirectMode = "DIRECT";

        internal static bool TryValidateProxy(string value, out string normalized, out string error)
        {
            normalized = (value ?? "").Trim();
            error = "";
            if (normalized.Length == 0) { error = "请输入 HTTP(S) 代理地址。"; return false; }
            Uri uri;
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0 ||
                (uri.AbsolutePath != "/" && uri.AbsolutePath.Length != 0) ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                normalized.IndexOf('"') >= 0 || normalized.IndexOf('\r') >= 0 || normalized.IndexOf('\n') >= 0)
            {
                error = "代理地址须为 HTTP/HTTPS URL，不含账号密码或路径，例如 http://127.0.0.1:7890。";
                return false;
            }
            normalized = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            return true;
        }

        internal static void Apply(string mode, string proxyUrl)
        {
            EnableTls12();
            string selectedMode = NormalizeMode(mode);
            if (selectedMode == CustomMode)
            {
                string normalized, error;
                if (!TryValidateProxy(proxyUrl, out normalized, out error)) throw new InvalidDataException(error);
                proxyUrl = normalized;
                WebRequest.DefaultWebProxy = new WebProxy(proxyUrl);
                SetProxyEnvironment(proxyUrl);
                Environment.SetEnvironmentVariable("FGS_SETUP_PROXY", proxyUrl, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("PIP_PROXY", proxyUrl, EnvironmentVariableTarget.Process);
            }
            else if (selectedMode == DirectMode)
            {
                WebRequest.DefaultWebProxy = null;
                ClearProxyEnvironment();
            }
            else
            {
                WebRequest.DefaultWebProxy = WebRequest.GetSystemWebProxy();
                RestoreInitialProxyEnvironment();
                ClearBypassEnvironment();
                string inherited = Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? Environment.GetEnvironmentVariable("https_proxy") ??
                    Environment.GetEnvironmentVariable("HTTP_PROXY") ?? Environment.GetEnvironmentVariable("http_proxy") ?? "";
                if (String.IsNullOrWhiteSpace(inherited))
                {
                    try
                    {
                        IWebProxy system = WebRequest.GetSystemWebProxy();
                        Uri target = new Uri("https://api.github.com/");
                        Uri resolved = system == null ? target : system.GetProxy(target);
                        if (resolved != null && !resolved.Equals(target)) inherited = resolved.GetLeftPart(UriPartial.Authority);
                    }
                    catch { }
                }
                string normalizedSystemProxy, validationError;
                if (!String.IsNullOrWhiteSpace(inherited) && TryValidateProxy(inherited, out normalizedSystemProxy, out validationError))
                    SetProxyEnvironment(normalizedSystemProxy);
                Environment.SetEnvironmentVariable("FGS_SETUP_PROXY", inherited, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("PIP_PROXY", inherited, EnvironmentVariableTarget.Process);
            }
            Environment.SetEnvironmentVariable("FGS_PROXY_MODE", selectedMode, EnvironmentVariableTarget.Process);
        }

        internal static IWebProxy CreateProxy(string mode, string proxyUrl)
        {
            EnableTls12();
            string selectedMode = NormalizeMode(mode);
            if (selectedMode == DirectMode) return null;
            if (selectedMode == CustomMode)
            {
                string normalized, error;
                if (!TryValidateProxy(proxyUrl, out normalized, out error)) throw new InvalidDataException(error);
                return new WebProxy(normalized);
            }
            string environmentProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? Environment.GetEnvironmentVariable("https_proxy") ??
                Environment.GetEnvironmentVariable("HTTP_PROXY") ?? Environment.GetEnvironmentVariable("http_proxy") ?? "";
            if (!String.IsNullOrWhiteSpace(environmentProxy))
            {
                string normalized, error;
                if (TryValidateProxy(environmentProxy, out normalized, out error)) return new WebProxy(normalized);
            }
            return WebRequest.GetSystemWebProxy();
        }

        internal static bool TestProxy(string mode, string proxyUrl, out string detail)
        {
            detail = "";
            try
            {
                EnableTls12();
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(FgsimTextureCore.TextureUrl);
                request.Proxy = CreateProxy(mode, proxyUrl);
                request.Method = "HEAD";
                request.Timeout = 15000;
                request.ReadWriteTimeout = 15000;
                request.UserAgent = "FilmGrainStudio-NetworkProxy-Test";
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    detail = "HTTP " + ((int)response.StatusCode).ToString() + " " + response.StatusDescription;
                    return (int)response.StatusCode >= 200 && (int)response.StatusCode < 400;
                }
            }
            catch (Exception ex) { detail = DescribeException(ex); return false; }
        }

        internal static void EnableTls12()
        {
            // The preview is compiled against .NET Framework 4.0, whose TLS defaults can stop at TLS 1.0.
            // Use the numeric enum value so this remains compilable with the v4.0 reference assemblies.
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        }

        private static string DescribeException(Exception error)
        {
            StringBuilder result = new StringBuilder();
            for (Exception current = error; current != null; current = current.InnerException)
            {
                if (result.Length > 0) result.Append(" -> ");
                result.Append(current.GetType().Name).Append(": ").Append(current.Message);
            }
            return result.ToString();
        }

        private static string NormalizeMode(string mode)
        {
            string value = (mode ?? "").Trim().ToUpperInvariant();
            if (value == CustomMode || value == DirectMode) return value;
            return SystemMode;
        }

        private static void SetProxyEnvironment(string proxy)
        {
            foreach (string name in EnvironmentProxyNames) Environment.SetEnvironmentVariable(name, proxy, EnvironmentVariableTarget.Process);
            ClearBypassEnvironment();
        }

        private static void ClearProxyEnvironment()
        {
            string[] names = new string[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy", "PIP_PROXY", "FGS_SETUP_PROXY" };
            foreach (string name in names) Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
            ClearBypassEnvironment();
        }

        private static void ClearBypassEnvironment()
        {
            Environment.SetEnvironmentVariable("NO_PROXY", "", EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("no_proxy", "", EnvironmentVariableTarget.Process);
        }

        private static string[] CaptureInitialProxyEnvironment()
        {
            string[] values = new string[EnvironmentProxyNames.Length];
            for (int i = 0; i < EnvironmentProxyNames.Length; i++) values[i] = Environment.GetEnvironmentVariable(EnvironmentProxyNames[i]);
            return values;
        }

        private static void RestoreInitialProxyEnvironment()
        {
            for (int i = 0; i < EnvironmentProxyNames.Length; i++) Environment.SetEnvironmentVariable(EnvironmentProxyNames[i], InitialProxyEnvironment[i], EnvironmentVariableTarget.Process);
        }
    }
}
