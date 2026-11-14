using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MacroKit
{
    /// <summary>Что лежит в последнем релизе на GitHub.</summary>
    public sealed class UpdateInfo
    {
        public Version Version;
        public string Tag;
        public string Notes;
        public string PageUrl;
        public string AssetName;
        public string AssetUrl;
        public long AssetSize;

        /// <summary>SHA-256 установщика из API GitHub (поле digest), если GitHub его отдал.</summary>
        public string Sha256;
    }

    /// <summary>
    /// Обновление через релизы GitHub — та же схема, что у Aion Config:
    /// релиз vX.Y.Z с установщиком MacroKit-Setup-X.Y.Z.exe. Программа смотрит
    /// последний релиз, скачивает установщик во временную папку, запускает его
    /// в тихом режиме и закрывается; установщик заменяет файлы и запускает
    /// новую версию сам.
    ///
    /// Репозиторий публичный, поэтому токен не нужен. Без токена у API лимит
    /// 60 запросов в час — с запасом для проверки при запуске и по кнопке.
    /// </summary>
    public static class Updater
    {
        public const string Repo = "awakkarts-ux/macrokit";
        const string Api = "https://api.github.com/repos/" + Repo + "/releases/latest";
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases";

        /// <summary>Версия этой сборки: из VERSION через build.ps1 (атрибут сборки).</summary>
        public static Version Current
        {
            get { return Normalize(Assembly.GetExecutingAssembly().GetName().Version); }
        }

        public static string CurrentText
        {
            get { return Text(Current); }
        }

        public static string Text(Version v)
        {
            return v == null ? "?" : v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);
        }

        static Version Normalize(Version v)
        {
            if (v == null) return new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        static void PrepareTls()
        {
            // .NET Framework на старых сборках Windows по умолчанию не включает
            // TLS 1.2, а GitHub другого не принимает
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        static HttpWebRequest Request(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "MacroKit-Updater";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 30000;
            req.AllowAutoRedirect = true;
            return req;
        }

        /// <summary>
        /// Последний релиз. Блокирующий вызов — звать не из потока интерфейса.
        /// Бросает исключение, если сети нет или ответ не разобрался.
        /// </summary>
        public static UpdateInfo FetchLatest()
        {
            PrepareTls();

            var req = Request(Api);
            req.Accept = "application/vnd.github+json";

            string json;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                json = sr.ReadToEnd();

            var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("GitHub вернул неожиданный ответ");

            var info = new UpdateInfo
            {
                Tag = Str(root, "tag_name"),
                Notes = Str(root, "body"),
                PageUrl = Str(root, "html_url")
            };

            Version v;
            string tag = (info.Tag ?? "").TrimStart('v', 'V');
            if (!Version.TryParse(tag, out v)) throw new InvalidDataException("Непонятная версия релиза: " + info.Tag);
            info.Version = Normalize(v);

            object rawAssets;
            if (root.TryGetValue("assets", out rawAssets) && rawAssets is object[])
            {
                foreach (var item in (object[])rawAssets)
                {
                    var a = item as Dictionary<string, object>;
                    if (a == null) continue;

                    string name = Str(a, "name") ?? "";
                    if (!name.StartsWith("MacroKit-Setup", StringComparison.OrdinalIgnoreCase) ||
                        !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                    info.AssetName = name;
                    info.AssetUrl = Str(a, "browser_download_url");

                    object size;
                    if (a.TryGetValue("size", out size) && size != null)
                        info.AssetSize = Convert.ToInt64(size, CultureInfo.InvariantCulture);

                    string digest = Str(a, "digest");
                    if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        info.Sha256 = digest.Substring(7).ToLowerInvariant();
                    break;
                }
            }

            return info;
        }

        public static bool IsNewer(UpdateInfo info)
        {
            return info != null && info.Version != null && info.Version > Current;
        }

        /// <summary>
        /// Скачать установщик во временную папку. progress — проценты 0–100.
        /// Размер и (если GitHub его дал) SHA-256 сверяются: битый или подменённый
        /// файл не запускается.
        /// </summary>
        public static string Download(UpdateInfo info, Action<int> progress)
        {
            if (info == null || string.IsNullOrEmpty(info.AssetUrl))
                throw new InvalidOperationException("В релизе " + (info == null ? "" : info.Tag) + " нет установщика");

            PrepareTls();

            string path = Path.Combine(Path.GetTempPath(), info.AssetName);
            string part = path + ".part";

            using (var resp = (HttpWebResponse)Request(info.AssetUrl).GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write))
            {
                long total = info.AssetSize > 0 ? info.AssetSize : resp.ContentLength;
                var buf = new byte[81920];
                long got = 0;
                int n, last = -1;

                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    got += n;
                    if (total > 0 && progress != null)
                    {
                        int pct = (int)(got * 100 / total);
                        if (pct != last) { last = pct; progress(pct); }
                    }
                }
            }

            long actual = new FileInfo(part).Length;
            if (info.AssetSize > 0 && actual != info.AssetSize)
            {
                TryDelete(part);
                throw new InvalidDataException("Установщик скачался не целиком (" + actual + " из " + info.AssetSize + " байт)");
            }

            if (info.Sha256 != null)
            {
                string hash;
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(part))
                    hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();

                if (hash != info.Sha256)
                {
                    TryDelete(part);
                    throw new InvalidDataException("Контрольная сумма установщика не совпала — файл не запущен");
                }
            }

            TryDelete(path);
            File.Move(part, path);
            return path;
        }

        /// <summary>
        /// Запустить установщик тихо: он сам закроет программу, если она ещё
        /// жива, заменит файлы и запустит новую версию. После вызова программа
        /// должна закрыться.
        /// </summary>
        public static void RunInstaller(string path)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
                UseShellExecute = true
            });
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }
    }
}
