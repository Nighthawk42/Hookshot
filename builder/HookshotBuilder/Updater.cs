using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace HookshotBuilder;

/// The game's update offer (host/hos_update.c) starts this app with --update <game folder> --tag <release>. When these tools are
/// not that release, the release's HookshotBuilder.zip is downloaded and unpacked beside this app, and the new app is started with
/// --fetched to build the folder; when they are, the folder is built again here (nx2/make_game.py --update: from the game files the
/// folder keeps).
static class Updater
{
    const string Latest = "https://api.github.com/repos/NotRexed/Hookshot/releases/latest";
    const string Asset = "HookshotBuilder.zip";
    const string SetupAsset = "HookshotBuilder.exe";            // (the setup, builder/setup/setup.c: a release holds it and its list)
    const string SetupList = "hookshot-setup.txt";              // (scripts/make_setup_parts.py)
    static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(20) };

    public static void FromCommandLine(MainWindow w)
    {
        var a = Environment.GetCommandLineArgs();
        string? folder = null, tag = null;
        var fetched = false;
        for (var i = 1; i < a.Length; i++)
        {
            if (a[i] == "--update" && i + 1 < a.Length) folder = a[++i];
            else if (a[i] == "--tag" && i + 1 < a.Length) tag = a[++i];
            else if (a[i] == "--fetched") fetched = true;
        }
        if (folder == null) return;
        UpdateFolder(w, folder, tag, fetched);
    }

    /// The latest release's tag, quietly (null: GitHub did not answer); a few tries, 20 s at most
    public static async Task<string?> LatestTag()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HookshotBuilder");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Latest);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    return doc.RootElement.GetProperty("tag_name").GetString();
                }
                if ((int)response.StatusCode < 500) break;   // (the API's limit, 60 an hour a PC: the list below instead)
            }
            catch (Exception) { }
            await Task.Delay(TimeSpan.FromSeconds(2 << attempt));
        }
        // (v2.0.0: when the API did not answer, the builder said a game was up to date. The release's setup list names its tag in
        // its first line, from GitHub's download service, not the API)
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var list = await http.GetStringAsync("https://github.com/NotRexed/Hookshot/releases/latest/download/" + SetupList);
                foreach (var line in list.Split('\n'))
                    if (line.StartsWith("tag ")) return line[4..].Trim();
            }
            catch (Exception) { }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        return null;
    }

    /// The latest release installed (only the parts that changed, as its setup would) and its builder started with args; this one
    /// closes. (no args: the new builder opens as it does)
    public static async Task RunLatestSetup(MainWindow w, IEnumerable<string> args)
    {
        var token = w.DownloadToken;
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HookshotBuilder");
        w.Note("Asking GitHub for the latest release");
        string? listUrl = null, listApiUrl = null;
        using (var response = await GetWithRetries(w, http, Latest, token, "application/vnd.github+json", TimeSpan.FromSeconds(30)))
        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)))
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
                if (asset.GetProperty("name").GetString() == SetupList)
                {
                    listUrl = asset.GetProperty("browser_download_url").GetString();
                    listApiUrl = asset.GetProperty("url").GetString();
                }
        if (listUrl == null) throw new InvalidOperationException("The latest release has no " + SetupList + ".");
        w.Note("Downloading the new Hookshot Builder");
        var tag = await Install(w, http, listUrl, listApiUrl, token);
        var exe = Path.Combine(VersionDir(tag), "HookshotBuilder", "HookshotBuilder.exe");
        var si = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var arg in args) si.ArgumentList.Add(arg);
        Process.Start(si);
        Application.Current.Shutdown();
    }

    static string AppData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HookshotBuilder");

    static string VersionDir(string tag) =>
        Path.Combine(AppData, "versions", string.Concat(tag.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')));

    /// A release's build tools as installed under versions\<tag> (by Install or by the setup), when complete
    internal static Tools? InstalledTools(string tag)
    {
        var dir = VersionDir(tag);
        var root = Path.Combine(dir, "HookshotBuilder", "tools");
        var python = Path.Combine(root, "python", "python.exe");
        if (!File.Exists(Path.Combine(dir, "installed.txt")) || !File.Exists(Path.Combine(root, "nx2", "make_game.py")) || !File.Exists(python))
            return null;
        var tools = new Tools(root, python);
        return tools.Version == tag ? tools : null;
    }

    sealed record Part(string Sha, long Size, string Dest, string From, string To, string Url);

    /// What the release's setup does (builder/setup/setup.c), done here: its list (hookshot-setup.txt) read, each part reused from
    /// downloads\ when its SHA-256 matches or downloaded and checked, unpacked into versions\<tag>.part and renamed once complete,
    /// current.txt and previous.txt kept as the setup keeps them. The list's tag is returned.
    static async Task<string> Install(MainWindow w, HttpClient http, string listUrl, string? listApiUrl, CancellationToken token)
    {
        var downloads = Path.Combine(AppData, "downloads");
        var versions = Path.Combine(AppData, "versions");
        var cache = Path.Combine(AppData, "update");
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(versions);
        Directory.CreateDirectory(cache);
        var listPath = Path.Combine(cache, SetupList);
        await DownloadWithRetries(w, http, listUrl, listApiUrl, listPath, 0, token);

        string? tag = null, run = null;
        var parts = new List<Part>();
        foreach (var line in await File.ReadAllLinesAsync(listPath, token))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 2 && f[0] == "tag") tag = f[1];
            else if (f.Length >= 2 && f[0] == "run") run = f[1];
            else if (f.Length >= 7 && f[0] == "part" && long.TryParse(f[2], out var size))
                parts.Add(new Part(f[1].ToLowerInvariant(), size, f[3], f[4], f[5], f[6]));
        }
        if (tag == null || run == null || parts.Count == 0) throw new InvalidOperationException("The release's setup list could not be read. Try again later.");

        var final = VersionDir(tag);
        if (InstalledTools(tag) != null && File.Exists(Path.Combine(final, run.Replace('/', '\\')))) return tag;
        var staging = final + ".part";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        if (Directory.Exists(final)) Directory.Delete(final, true);
        Directory.CreateDirectory(staging);
        for (var i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            var k = i;
            var zip = Path.Combine(downloads, p.Sha + ".zip");
            if (!(File.Exists(zip) && await Sha256(zip, token) == p.Sha))
            {
                w.Note($"Downloading part {k + 1} of {parts.Count} ({p.Size >> 20} MB)");
                var temp = zip + ".part";
                await DownloadWithRetries(w, http, p.Url, null, temp, p.Size, token, f => w.DownloadProgress((k + f) / parts.Count));
                if (await Sha256(temp, token) != p.Sha)
                {
                    File.Delete(temp);
                    throw new InvalidDataException($"Part {k + 1} of {parts.Count} did not download correctly (its checksum differs). Try again.");
                }
                File.Move(temp, zip, true);
            }
            w.Note($"Unpacking part {k + 1} of {parts.Count}");
            var dest = p.Dest == "." ? staging : Path.Combine(staging, p.Dest.Replace('/', '\\'));
            Directory.CreateDirectory(dest);
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, dest, overwriteFiles: true), token);
            if (p.From != "-" && p.To != "-")   // (llvm-mingw-<version>-ucrt-x86_64 -> llvm-mingw)
            {
                var to = Path.Combine(dest, p.To);
                if (!Directory.Exists(to)) Directory.Move(Path.Combine(dest, p.From), to);
            }
            w.DownloadProgress((k + 1.0) / parts.Count);
        }
        var exe = Path.Combine(staging, run.Replace('/', '\\'));
        if (!File.Exists(exe)) throw new FileNotFoundException($"The builder is missing after unpacking ({exe}). Your antivirus may have removed it.");
        Directory.Move(staging, final);
        File.WriteAllText(Path.Combine(final, "installed.txt"), tag);

        // (as the setup's prune(): the version just installed and the one before it stay; older versions and their downloads go)
        var current = Path.Combine(versions, "current.txt");
        var before = File.Exists(current) ? File.ReadAllText(current).Trim() : "";
        if (before.Length > 0 && before != tag) File.WriteAllText(Path.Combine(versions, "previous.txt"), before);
        File.WriteAllText(current, tag);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(final) };
        if (before.Length > 0) keep.Add(Path.GetFileName(VersionDir(before)));
        foreach (var dir in Directory.GetDirectories(versions))
            if (!keep.Contains(Path.GetFileName(dir)))
                try { Directory.Delete(dir, true); } catch (Exception) { }
        var wanted = new HashSet<string>(parts.Select(p => p.Sha + ".zip"), StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(downloads))
            if (!wanted.Contains(Path.GetFileName(file)))
                try { File.Delete(file); } catch (Exception) { }
        return tag;
    }

    static async Task<string> Sha256(string path, CancellationToken token)
    {
        await using var s = File.OpenRead(path);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(s, token)).ToLowerInvariant();
    }

    /// The game in folder built again with the latest release: here when these tools are it (or fetched), else after the setup
    /// installed it (it starts the new builder with --update folder --tag t --fetched)
    public static async void UpdateFolder(MainWindow w, string folder, string? tag, bool fetched)
    {
        w.BeginUpdate(folder, tag);
        var tools = Tools.Find();
        if (tools == null)
        {
            w.DownloadFailed("The Hookshot tools were not found next to this app.");
            return;
        }
        // (just downloaded, but another release than asked for: the setup installed the release before it (GitHub's latest link lags
        // right after a publish), and building here kept players on the old version while the builder said it updated)
        if (fetched && tag != null && tools.Version != null && tools.Version != tag)
        {
            w.DownloadFailed($"The update installed {tools.Version}, not {tag}: GitHub's download links can lag right after a release. Try Update again in a few minutes.");
            return;
        }
        // (already the new release, or just downloaded: no second download)
        if (fetched || (tag != null && tools.Version == tag))
        {
            w.UpdateBuild(tools, folder);
            return;
        }
        // (that release already installed under versions\, by an update before this one or by the setup: its tools build the folder)
        if (tag != null && InstalledTools(tag) is { } installed)
        {
            w.UpdateBuild(installed, folder);
            return;
        }
        try
        {
            var token = w.DownloadToken;
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("HookshotBuilder");
            w.Note("Asking GitHub for the latest release");
            string? newTag = null, url = null, apiUrl = null, listUrl = null, listApiUrl = null;
            long size = 0;
            using (var response = await GetWithRetries(w, http, Latest, token, "application/vnd.github+json", TimeSpan.FromSeconds(30)))
            using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)))
            {
                newTag = doc.RootElement.GetProperty("tag_name").GetString();
                foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
                {
                    var assetName = asset.GetProperty("name").GetString();
                    if (assetName == Asset)
                    {
                        url = asset.GetProperty("browser_download_url").GetString();
                        apiUrl = asset.GetProperty("url").GetString();
                        size = asset.GetProperty("size").GetInt64();
                    }
                    else if (assetName == SetupList)
                    {
                        listUrl = asset.GetProperty("browser_download_url").GetString();
                        listApiUrl = asset.GetProperty("url").GetString();
                    }
                }
            }
            if (newTag == null || (url == null && listUrl == null)) throw new InvalidOperationException("The latest release has no " + Asset + ".");
            if (newTag == tools.Version)
            {
                w.UpdateBuild(tools, folder);
                return;
            }
            if (InstalledTools(newTag) is { } have)
            {
                w.UpdateBuild(have, folder);
                return;
            }
            var name = string.Concat(newTag.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.'));
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HookshotBuilder", "update");
            Directory.CreateDirectory(cache);
            // (a release with the setup's list: its parts installed here, as the setup would (only the parts that changed are downloaded),
            // and the folder built with that release's tools by this app, so the folder's update offer keeps coming back to this builder
            // and its launcher. The setup itself is not run: Defender's heuristic quarantines it, Trojan:Win32/Sabsik.FL.A!ml, 2026-11-09)
            if (listUrl != null)
            {
                w.Note($"Downloading the update for {newTag}");
                var listed = await Install(w, http, listUrl, listApiUrl, token);
                if (listed != newTag)
                {
                    w.DownloadFailed($"The update list is for {listed}, not {newTag}: GitHub's download links can lag right after a release. Try Update again in a few minutes.");
                    return;
                }
                w.UpdateBuild(InstalledTools(newTag) ?? throw new InvalidOperationException($"{newTag} was installed but its build tools are incomplete."), folder);
                return;
            }
            if (url == null) throw new InvalidOperationException("The latest release has no " + Asset + ".");
            var zip = Path.Combine(cache, "HookshotBuilder-" + name + ".zip");
            w.Note($"Downloading {Asset} of {newTag} ({size >> 20} MB)");
            await DownloadWithRetries(w, http, url, apiUrl, zip, size, token, w.DownloadProgress);
            w.Note("Unpacking the new version");
            // (beside this app when that folder takes writing, else in the app data folder)
            var parent = Directory.GetParent(AppContext.BaseDirectory.TrimEnd('\\', '/'))!.FullName;
            string target;
            try
            {
                target = await Unpack(zip, Path.Combine(parent, "HookshotBuilder " + name));
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                target = await Unpack(zip, Path.Combine(Path.GetDirectoryName(cache)!, "builders", name));
            }
            try { File.Delete(zip); } catch (Exception) { }
            var exe = Path.Combine(target, "HookshotBuilder.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("The download has no HookshotBuilder.exe (your antivirus may have removed it).");
            var si = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = target };
            foreach (var arg in new[] { "--update", folder, "--tag", newTag, "--fetched" }) si.ArgumentList.Add(arg);
            Process.Start(si);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            w.DownloadFailed("The download was cancelled. Your game folder is unchanged.");
        }
        catch (Exception e)
        {
            w.DownloadFailed(e.Message + " Your game folder is unchanged; you can also download the new version from GitHub yourself.");
        }
    }

    static async Task<HttpResponseMessage> GetWithRetries(MainWindow w, HttpClient http, string url, CancellationToken token,
        string? accept = null, TimeSpan? timeout = null)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (accept != null) request.Headers.Accept.ParseAdd(accept);
                using var ask = timeout is null ? null : new CancellationTokenSource(timeout.Value);
                using var both = ask is null ? null : CancellationTokenSource.CreateLinkedTokenSource(token, ask.Token);
                var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, both?.Token ?? token);
                if (response.IsSuccessStatusCode) return response;

                var failure = HttpFailure(response);
                response.Dispose();
                if (!IsTransient(failure)) throw failure;
                last = failure;
            }
            catch (OperationCanceledException e) when (!token.IsCancellationRequested && timeout is not null)
            {
                last = e;
            }
            catch (HttpRequestException e) when (IsTransient(e))
            {
                last = e;
            }

            if (attempt == RetryDelays.Length) break;
            w.Note($"GitHub did not answer{StatusDetail(last)}. Retrying in {(int)RetryDelays[attempt].TotalSeconds} s...");
            await Task.Delay(RetryDelays[attempt], token);
        }
        throw new HttpRequestException($"GitHub's release API did not respond after {RetryDelays.Length + 1} attempts: {last?.Message}",
            last, (last as HttpRequestException)?.StatusCode);
    }

    static async Task DownloadWithRetries(MainWindow w, HttpClient http, string url, string? apiUrl, string path, long size,
        CancellationToken token, Action<double>? progress = null)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            try
            {
                await DownloadOnce(http, url, path, size, token, progress, apiAsset: false);
                return;
            }
            catch (Exception e) when (IsTransient(e))
            {
                last = e;
                if (apiUrl != null) w.Note($"Release download failed{StatusDetail(e)}; trying GitHub's API asset URL.");
            }

            if (apiUrl != null)
            {
                try
                {
                    await DownloadOnce(http, apiUrl, path, size, token, progress, apiAsset: true);
                    return;
                }
                catch (Exception e) when (IsTransient(e))
                {
                    last = e;
                }
            }

            if (attempt == RetryDelays.Length) break;
            w.Note($"GitHub's download servers did not answer{StatusDetail(last)}. Retrying in {(int)RetryDelays[attempt].TotalSeconds} s...");
            await Task.Delay(RetryDelays[attempt], token);
        }
        throw new HttpRequestException($"GitHub's asset download failed after {RetryDelays.Length + 1} attempts: {last?.Message}",
            last, (last as HttpRequestException)?.StatusCode);
    }

    static async Task DownloadOnce(HttpClient http, string url, string path, long size, CancellationToken token,
        Action<double>? progress, bool apiAsset)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (apiAsset) request.Headers.Accept.ParseAdd("application/octet-stream");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw HttpFailure(response);

        var total = response.Content.Headers.ContentLength ?? size;
        await using var from = await response.Content.ReadAsStreamAsync(token);
        await using var to = File.Create(path);
        var buffer = new byte[1 << 20];
        long have = 0;
        var shown = DateTime.MinValue;
        int n;
        while (true)
        {
            try { n = await from.ReadAsync(buffer, token); }
            catch (IOException e) { throw new HttpRequestException("The download connection ended unexpectedly.", e); }
            if (n == 0) break;
            await to.WriteAsync(buffer.AsMemory(0, n), token);
            have += n;
            if (progress != null && total > 0 && DateTime.UtcNow - shown > TimeSpan.FromMilliseconds(200))
            {
                shown = DateTime.UtcNow;
                progress(Math.Min(1.0, (double)have / total));
            }
        }
        if (total > 0 && have != total) throw new HttpRequestException($"The download ended early ({have >> 20} of {total >> 20} MB).");
    }

    static HttpRequestException HttpFailure(HttpResponseMessage response) =>
        new($"GitHub returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).", null, response.StatusCode);

    static bool IsTransient(Exception error) =>
        error is HttpRequestException request && (request.StatusCode is null || (int)request.StatusCode >= 500);

    static string StatusDetail(Exception? error) =>
        error is HttpRequestException { StatusCode: { } status } ? $" (HTTP {(int)status})" : "";

    /// The zip's HookshotBuilder folder, as target (an older one there replaced)
    static async Task<string> Unpack(string zip, string target)
    {
        var part = target + ".part";
        if (Directory.Exists(part)) Directory.Delete(part, true);
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, part));
        var inner = Path.Combine(part, "HookshotBuilder");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(Directory.Exists(inner) ? inner : part, target);
        if (Directory.Exists(part)) Directory.Delete(part, true);
        return target;
    }
}
