using System.IO;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net;
using PCL.Network;

namespace PCL;

/// <summary>作者清单里与更新检查相关的字段。</summary>
internal sealed record ModpackManifestInfo(string Name, string Version, string PackageUrl);

/// <summary>
///     按实例配置的作者清单（HTTPS 静态 JSON）。只用于检查版本与取得整合包直链，
///     不参与文件事务；清单缺失或出错时不影响手动更新。
/// </summary>
internal static class InstanceModpackManifest
{
    private const int MaxBytes = 512 * 1024;

    internal static string? GetUrl(McInstance? instance) =>
        instance is null ? null : Normalize(States.Instance.ModpackManifestUrl[instance.PathInstance]);

    internal static void SetUrl(McInstance instance, string? url) =>
        States.Instance.ModpackManifestUrl[instance.PathInstance] = Normalize(url) ?? "";

    internal static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    internal static ModpackManifestInfo Fetch(string url, CancellationToken token)
    {
        if (!IsHttps(url))
            throw new IOException(Lang.Text("Instance.ModpackUpdate.Manifest.InvalidUrl"));

        string text;
        try
        {
            using var response = NetworkService.GetClient()
                .GetAsync(new Uri(url), HttpCompletionOption.ResponseContentRead, token)
                .GetAwaiter().GetResult();
            Requester.EnsureSuccess(response);
            // 只接受仍然停在 HTTPS 上的结果，避免跳转到明文地址
            if (response.RequestMessage?.RequestUri is { Scheme: not "https" })
                throw new IOException(Lang.Text("Instance.ModpackUpdate.Manifest.InsecureRedirect"));
            if (response.Content.Headers.ContentLength > MaxBytes)
                throw new IOException(Lang.Text("Instance.ModpackUpdate.Manifest.TooLarge"));
            text = response.Content.ReadAsStringAsync(token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException(Lang.Text("Instance.ModpackUpdate.Manifest.FetchFailed", ex.Message), ex);
        }

        if (text.Length > MaxBytes)
            throw new IOException(Lang.Text("Instance.ModpackUpdate.Manifest.TooLarge"));

        JObject json;
        try
        {
            json = JObject.Parse(text);
        }
        catch (Exception ex)
        {
            throw new IOException(Lang.Text("Instance.ModpackUpdate.Manifest.InvalidJson", ex.Message), ex);
        }

        var version = json.Value<string>("version")?.Trim() ?? "";
        if (version.Length == 0)
            throw new IOException(Lang.Text("Instance.ModpackUpdate.Manifest.MissingVersion"));
        var package = (json.Value<string>("package") ?? json.Value<string>("url") ?? "").Trim();
        if (!Uri.TryCreate(package, UriKind.Absolute, out var packageUri)
            || (packageUri.Scheme != Uri.UriSchemeHttp && packageUri.Scheme != Uri.UriSchemeHttps))
            throw new IOException(Lang.Text("Instance.ModpackUpdate.Manifest.MissingPackage"));
        return new ModpackManifestInfo(json.Value<string>("name")?.Trim() ?? "", version, packageUri.ToString());
    }

    internal static string Download(string url, string destination, CancellationToken token)
    {
        try
        {
            using var response = NetworkService.GetClient()
                .GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, token)
                .GetAwaiter().GetResult();
            Requester.EnsureSuccess(response);
            var folder = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            using var input = response.Content.ReadAsStream(token);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                81920);
            input.CopyTo(output);
            return destination;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException(Lang.Text("Instance.ModpackUpdate.Source.DownloadFailed", ex.Message), ex);
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
