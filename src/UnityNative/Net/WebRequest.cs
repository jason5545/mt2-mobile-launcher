// UnityWebRequest：本機檔（file://、或沒有協定的絕對路徑）照讀；其他網址一律回「連不上」（移植版離線，不連開發商的後端）。
// 請求在送出時就完成（AsyncOperation 馬上 isDone），等的協程下一格繼續，跟 Unity 讀本機檔的時序接近。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Networking;

namespace UnityNative
{
    public sealed class NWebRequest
    {
        public string Url = "";
        public UnityWebRequest.UnityWebRequestMethod Method = UnityWebRequest.UnityWebRequestMethod.Get;
        public string CustomMethod;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>();
        public DownloadHandler Download;
        public UploadHandler Upload;
        public bool Sent, Done;
        public long ResponseCode;
        public UnityWebRequest.UnityWebRequestError Error;
        public UnityWebRequest.Result Result = UnityWebRequest.Result.InProgress;
        public int Timeout, RedirectLimit = 32;
        public bool Chunked, Use100Continue = true, SuppressErrors;
        public ulong Downloaded;
    }

    public sealed class NDownload
    {
        public byte[] Data = Array.Empty<byte>();
        public GCHandle Pin;
        public bool Done;
        public string Error, ContentType;
        public string FilePath;
        public string Url;
    }

    public static class Web
    {
        static readonly Dictionary<IntPtr, NWebRequest> reqs = new Dictionary<IntPtr, NWebRequest>();
        static readonly Dictionary<IntPtr, NDownload> downloads = new Dictionary<IntPtr, NDownload>();
        static readonly Dictionary<IntPtr, byte[]> uploads = new Dictionary<IntPtr, byte[]>();
        static long next = 1;
        static readonly object gate = new object();

        public static IntPtr NewReq() { lock (gate) { var p = (IntPtr)next++; reqs[p] = new NWebRequest(); return p; } }
        public static NWebRequest R(UnityWebRequest r) { lock (gate) return reqs.TryGetValue(r.m_Ptr, out var n) ? n : (reqs[r.m_Ptr] = new NWebRequest()); }
        public static void Release(UnityWebRequest r) { lock (gate) reqs.Remove(r.m_Ptr); }
        public static IntPtr NewDownload(string url = null, string file = null) { lock (gate) { var p = (IntPtr)next++; downloads[p] = new NDownload { Url = url, FilePath = file }; return p; } }
        public static NDownload D(DownloadHandler d) { lock (gate) return d != null && downloads.TryGetValue(d.m_Ptr, out var n) ? n : null; }
        public static void ReleaseDownload(DownloadHandler d) { lock (gate) { if (downloads.TryGetValue(d.m_Ptr, out var n) && n.Pin.IsAllocated) n.Pin.Free(); downloads.Remove(d.m_Ptr); } }
        public static IntPtr NewUpload(byte[] data) { lock (gate) { var p = (IntPtr)next++; uploads[p] = data; return p; } }

        public static string LocalPath(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var p = Uri.UnescapeDataString(url.Substring(7));
                if (p.StartsWith("/") && p.Length > 2 && p[2] == ':') p = p.Substring(1);
                return p;
            }
            if (url.StartsWith("jar:", StringComparison.OrdinalIgnoreCase)) return null;
            if (!url.Contains("://") && (Path.IsPathRooted(url))) return url;
            return null;
        }

        public static UnityWebRequestAsyncOperation Begin(UnityWebRequest r)
        {
            var n = R(r);
            n.Sent = true;
            var path = LocalPath(n.Url);
            var dl = D(n.Download);
            if (path != null && File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                if (dl != null)
                {
                    if (dl.FilePath != null) File.WriteAllBytes(dl.FilePath, bytes);
                    dl.Data = bytes;
                    dl.Done = true;
                    OnDownloaded?.Invoke(n.Download, dl, path);
                }
                n.Downloaded = (ulong)bytes.Length;
                n.ResponseCode = 200;
                n.Result = UnityWebRequest.Result.Success;
            }
            else if (path != null)
            {
                n.ResponseCode = 404;
                n.Error = UnityWebRequest.UnityWebRequestError.Unknown;
                n.Result = UnityWebRequest.Result.ConnectionError;
                if (dl != null) { dl.Done = true; dl.Error = "Cannot open file " + path; }
            }
            else
            {
                n.ResponseCode = 0;
                n.Error = UnityWebRequest.UnityWebRequestError.CannotConnectToHost;
                n.Result = UnityWebRequest.Result.ConnectionError;
                if (dl != null) { dl.Done = true; dl.Error = "Cannot connect to destination host"; }
            }
            n.Done = true;
            var op = NAsyncOperation.New<NAsyncOperation>(typeof(UnityWebRequestAsyncOperation));
            op.Complete();
            return (UnityWebRequestAsyncOperation)op.Managed;
        }

        /// <summary>下載完成時（貼圖、聲音的 DownloadHandler 用來把位元組轉成素材）</summary>
        public static Action<DownloadHandler, NDownload, string> OnDownloaded;

        public static unsafe byte* Bytes(DownloadHandler d, out int length)
        {
            var n = D(d);
            if (n == null || n.Data.Length == 0) { length = 0; return null; }
            if (!n.Pin.IsAllocated) n.Pin = GCHandle.Alloc(n.Data, GCHandleType.Pinned);
            length = n.Data.Length;
            return (byte*)n.Pin.AddrOfPinnedObject();
        }
    }
}

namespace UnityNative.Icalls
{
    using UnityNative;
    using Err = UnityEngine.Networking.UnityWebRequest.UnityWebRequestError;

    public static unsafe partial class UnityEngine_Networking_UnityWebRequest
    {
        public static string GetWebErrorString(Err err) => err switch
        {
            Err.OK => null,
            Err.CannotConnectToHost => "Cannot connect to destination host",
            Err.CannotResolveHost => "Cannot resolve destination host",
            _ => err.ToString(),
        };
        public static string GetHTTPStatusString(long responseCode) => responseCode == 404 ? "HTTP/1.1 404 Not Found" : "HTTP/1.1 " + responseCode;
        public static void ClearCookieCache(string domain, string path) { }
        public static IntPtr Create() => Web.NewReq();
        public static void Release(UnityWebRequest self) => Web.Release(self);
        public static UnityWebRequestAsyncOperation BeginWebRequest(UnityWebRequest self) => Web.Begin(self);
        public static void Abort(UnityWebRequest self) { var n = Web.R(self); n.Done = true; n.Result = UnityWebRequest.Result.ConnectionError; }
        public static Err SetMethod(UnityWebRequest self, UnityWebRequest.UnityWebRequestMethod methodType) { Web.R(self).Method = methodType; return Err.OK; }
        public static Err SetCustomMethod(UnityWebRequest self, string customMethodName) { Web.R(self).CustomMethod = customMethodName; Web.R(self).Method = UnityWebRequest.UnityWebRequestMethod.Custom; return Err.OK; }
        public static UnityWebRequest.UnityWebRequestMethod GetMethod(UnityWebRequest self) => Web.R(self).Method;
        public static string GetCustomMethod(UnityWebRequest self) => Web.R(self).CustomMethod;
        public static Err GetError(UnityWebRequest self) => Web.R(self).Error;
        public static bool get_use100Continue(UnityWebRequest self) => Web.R(self).Use100Continue;
        public static void set_use100Continue(UnityWebRequest self, bool value) => Web.R(self).Use100Continue = value;
        public static string GetUrl(UnityWebRequest self) => Web.R(self).Url;
        public static Err SetUrl(UnityWebRequest self, string url) { Web.R(self).Url = url ?? ""; return Err.OK; }
        public static long get_responseCode(UnityWebRequest self) => Web.R(self).ResponseCode;
        public static float GetUploadProgress(UnityWebRequest self) => Web.R(self).Done ? 1f : 0f;
        public static bool IsExecuting(UnityWebRequest self) { var n = Web.R(self); return n.Sent && !n.Done; }
        public static bool get_isModifiable(UnityWebRequest self) => !Web.R(self).Sent;
        public static UnityWebRequest.Result get_result(UnityWebRequest self) => Web.R(self).Result;
        public static float GetDownloadProgress(UnityWebRequest self) => Web.R(self).Done ? 1f : 0f;
        public static ulong get_uploadedBytes(UnityWebRequest self) => 0;
        public static ulong get_downloadedBytes(UnityWebRequest self) => Web.R(self).Downloaded;
        public static int GetRedirectLimit(UnityWebRequest self) => Web.R(self).RedirectLimit;
        public static void SetRedirectLimitFromScripting(UnityWebRequest self, int limit) => Web.R(self).RedirectLimit = limit;
        public static bool GetChunked(UnityWebRequest self) => Web.R(self).Chunked;
        public static Err SetChunked(UnityWebRequest self, bool chunked) { Web.R(self).Chunked = chunked; return Err.OK; }
        public static string GetRequestHeader(UnityWebRequest self, string name) => Web.R(self).Headers.TryGetValue(name, out var v) ? v : null;
        public static Err InternalSetRequestHeader(UnityWebRequest self, string name, string value) { Web.R(self).Headers[name] = value; return Err.OK; }
        public static string GetResponseHeader(UnityWebRequest self, string name) => null;
        public static string[] GetResponseHeaderKeys(UnityWebRequest self) => Array.Empty<string>();
        public static Err SetUploadHandler(UnityWebRequest self, UploadHandler uh) { Web.R(self).Upload = uh; return Err.OK; }
        public static Err SetDownloadHandler(UnityWebRequest self, DownloadHandler dh) { Web.R(self).Download = dh; return Err.OK; }
        public static Err SetCertificateHandler(UnityWebRequest self, CertificateHandler ch) => Err.OK;
        public static int GetTimeoutMsec(UnityWebRequest self) => Web.R(self).Timeout;
        public static Err SetTimeoutMsec(UnityWebRequest self, int timeout) { Web.R(self).Timeout = timeout; return Err.OK; }
        public static bool GetSuppressErrorsToConsole(UnityWebRequest self) => Web.R(self).SuppressErrors;
        public static Err SetSuppressErrorsToConsole(UnityWebRequest self, bool suppress) { Web.R(self).SuppressErrors = suppress; return Err.OK; }
    }

    public static unsafe partial class UnityEngine_Networking_DownloadHandler
    {
        public static void Release(DownloadHandler self) => Web.ReleaseDownload(self);
        public static bool IsDone(DownloadHandler self) => Web.D(self)?.Done ?? false;
        public static string GetErrorMsg(DownloadHandler self) => Web.D(self)?.Error;
        public static string GetContentType(DownloadHandler self) => Web.D(self)?.ContentType;
        public static byte* InternalGetByteArray(DownloadHandler dh, out int length) => Web.Bytes(dh, out length);
    }

    public static unsafe partial class UnityEngine_Networking_DownloadHandlerBuffer
    {
        public static IntPtr Create(DownloadHandlerBuffer obj) => Web.NewDownload();
    }

    public static unsafe partial class UnityEngine_Networking_DownloadHandlerFile
    {
        public static IntPtr Create(DownloadHandlerFile obj, string path, bool append) => Web.NewDownload(null, path);
    }

    public static unsafe partial class UnityEngine_Networking_DownloadHandlerScript
    {
        public static IntPtr Create(DownloadHandlerScript obj) => Web.NewDownload();
        public static IntPtr CreatePreallocated(DownloadHandlerScript obj, byte[] preallocatedBuffer) => Web.NewDownload();
    }

    public static unsafe partial class UnityEngine_Networking_UploadHandler
    {
        public static void Release(UploadHandler self) { }
        public static string InternalGetContentType(UploadHandler self) => "application/octet-stream";
        public static void InternalSetContentType(UploadHandler self, string newContentType) { }
        public static float InternalGetProgress(UploadHandler self) => 1f;
    }

    public static unsafe partial class UnityEngine_Networking_UploadHandlerRaw
    {
        public static IntPtr Create(UploadHandlerRaw self, byte* data, int dataLength)
        {
            var b = new byte[Math.Max(0, dataLength)];
            if (data != null) Marshal.Copy((IntPtr)data, b, 0, b.Length);
            return Web.NewUpload(b);
        }
    }
}
