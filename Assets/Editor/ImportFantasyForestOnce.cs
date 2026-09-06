#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-shot importer for Asset Store product 35361 (Fantasy Forest Environment - Free Demo).
/// Uses the signed-in Editor Unity ID against packages-v2 (CN then global).
/// </summary>
[InitializeOnLoad]
public static class ImportFantasyForestOnce
{
    public const long ProductId = 35361;
    const string StatusAbs = @"C:\Users\dongx\FirstFPS\Logs\import-forest-status.txt";
    const string PrefsImported = "FirstFPS.ImportFantasyForest.Imported";
    const string SessionKey = "FirstFPS.ImportFantasyForest.Running";
    const string LoginPromptedKey = "FirstFPS.ImportFantasyForest.LoginPrompted";

    static readonly string[] ApiHosts =
    {
        "https://packages-v2.unity.cn",
        "https://packages-v2.unity.com"
    };

    static ImportFantasyForestOnce()
    {
        if (EditorPrefs.GetBool(PrefsImported, false))
            return;
        SessionState.SetBool(SessionKey, false);
        EditorApplication.delayCall += Kickoff;
    }

    [MenuItem("GameMesh/Import Fantasy Forest Environment")]
    public static void ImportMenu()
    {
        SessionState.SetBool(SessionKey, false);
        _ = RunAsync(force: true);
    }

    static void Kickoff()
    {
        if (EditorPrefs.GetBool(PrefsImported, false))
            return;
        if (DirectoryExistsImported())
        {
            EditorPrefs.SetBool(PrefsImported, true);
            WriteStatus("ALREADY_IMPORTED");
            return;
        }

        _ = RunAsync(force: true);
    }

    static async Task RunAsync(bool force)
    {
        if (SessionState.GetBool(SessionKey, false))
            return;
        SessionState.SetBool(SessionKey, true);

        try
        {
            for (var i = 0; i < 25 && (EditorApplication.isCompiling || EditorApplication.isUpdating); i++)
                await Task.Delay(400);

            WriteStatus("START user=" + CloudProjectSettings.userName
                + " compiling=" + EditorApplication.isCompiling
                + " updating=" + EditorApplication.isUpdating
                + " prefsImported=" + EditorPrefs.GetBool(PrefsImported, false));

            var token = await GetTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                WriteStatus("NEED_EDITOR_LOGIN website login is not enough; sign in at Unity Editor top-right");
                if (!SessionState.GetBool(LoginPromptedKey, false))
                {
                    SessionState.SetBool(LoginPromptedKey, true);
                    CloudProjectSettings.ShowLogin();
                }

                for (var i = 0; i < 90 && string.IsNullOrEmpty(token); i++)
                {
                    await Task.Delay(2000);
                    token = await GetTokenAsync();
                }

                if (string.IsNullOrEmpty(token))
                {
                    SessionState.SetBool(SessionKey, false);
                    WriteStatus("FAIL no Editor access token after waiting. Sign in via Editor account icon, then GameMesh > Import Fantasy Forest Environment");
                    return;
                }

                WriteStatus("TOKEN_OK user=" + CloudProjectSettings.userName);
            }

            string downloadJson = null;
            string usedHost = null;
            foreach (var host in ApiHosts)
            {
                var (ok, body, code) = await GetAsync(host + "/-/api/legacy-package-download-info/" + ProductId, token);
                WriteStatus("DOWNLOAD_INFO host=" + host + " http=" + code + " bytes=" + (body?.Length ?? 0));
                if (ok && !string.IsNullOrEmpty(body) && body.Contains("\"url\""))
                {
                    downloadJson = body;
                    usedHost = host;
                    break;
                }

                if (code == 401 || code == 403)
                {
                    WriteStatus("FAIL unauthorized on " + host + " body=" + Trim(body, 400));
                }
            }

            if (downloadJson == null)
            {
                foreach (var host in ApiHosts)
                {
                    var (ok, body, code) = await GetAsync(host + "/-/api/product/" + ProductId, token);
                    WriteStatus("PRODUCT host=" + host + " http=" + code + " " + Trim(body, 300));
                    var (ok2, body2, code2) = await GetAsync(host + "/-/api/purchases?offset=0&limit=20&query=Fantasy%20Forest", token);
                    WriteStatus("PURCHASES host=" + host + " http=" + code2 + " " + Trim(body2, 300));
                    if (ok && ok2)
                        usedHost = host;
                }

                WriteStatus("FAIL asset 35361 is not downloadable yet. Add it to My Assets on the Asset Store, then rerun GameMesh > Import Fantasy Forest Environment");
                return;
            }

            var url = ExtractJsonString(downloadJson, "url");
            var key = ExtractJsonString(downloadJson, "key");
            var packageName = ExtractJsonString(downloadJson, "filename_safe_package_name") ?? "FantasyForestEnvironment";
            var publisher = ExtractJsonString(downloadJson, "filename_safe_publisher_name") ?? "TriForgeAssets";
            var category = ExtractJsonString(downloadJson, "filename_safe_category_name") ?? "3D";
            if (string.IsNullOrEmpty(url))
            {
                WriteStatus("FAIL parse download url from " + Trim(downloadJson, 500));
                return;
            }

            WriteStatus("DOWNLOADING package=" + packageName + " keyLen=" + (key?.Length ?? 0));
            var destFile = await DownloadAndDecryptAsync(url, key, token, packageName, publisher, category);

            if (string.IsNullOrEmpty(destFile) || !File.Exists(destFile))
            {
                WriteStatus("FAIL could not download unitypackage");
                SessionState.SetBool(SessionKey, false);
                return;
            }

            WriteStatus("IMPORTING " + destFile + " size=" + new FileInfo(destFile).Length);
            AssetDatabase.importPackageCompleted += OnImported;
            AssetDatabase.importPackageFailed += OnImportFailed;
            AssetDatabase.ImportPackage(destFile, false);
        }
        catch (Exception ex)
        {
            WriteStatus("FAIL " + ex.GetType().Name + ": " + ex.Message);
            Debug.LogError("[ImportFantasyForest] " + ex);
            SessionState.SetBool(SessionKey, false);
        }
    }

    static void OnImported(string packageName)
    {
        AssetDatabase.importPackageCompleted -= OnImported;
        AssetDatabase.importPackageFailed -= OnImportFailed;
        EditorPrefs.SetBool(PrefsImported, true);
        WriteStatus("OK imported " + packageName + " user=" + CloudProjectSettings.userName);
        Debug.Log("[ImportFantasyForest] imported " + packageName);
        SessionState.SetBool(SessionKey, false);
    }

    static void OnImportFailed(string packageName, string error)
    {
        AssetDatabase.importPackageCompleted -= OnImported;
        AssetDatabase.importPackageFailed -= OnImportFailed;
        WriteStatus("FAIL import " + packageName + ": " + error);
        SessionState.SetBool(SessionKey, false);
    }

    static async Task<string> GetTokenAsync()
    {
        var packman = await GetPackmanTokenAsync();
        if (!string.IsNullOrEmpty(packman))
            return packman;

        WriteStatus("PACKMAN_TOKEN empty, falling back to Connect token");
        try
        {
            var task = CloudProjectSettings.GetServiceTokenAsync();
            if (task != null)
            {
                var token = await task;
                if (!string.IsNullOrEmpty(token))
                    return token;
            }
        }
        catch (Exception ex)
        {
            WriteStatus("TOKEN_ERROR " + ex.Message);
        }

        return TryConnectToken();
    }

    static async Task<string> GetPackmanTokenAsync()
    {
        var authTask = GetAuthCodeAsync("packman");
        var finished = await Task.WhenAny(authTask, Task.Delay(45000));
        if (finished != authTask)
        {
            WriteStatus("AUTH_CODE timeout");
            return null;
        }

        var authCode = await authTask;
        if (string.IsNullOrEmpty(authCode))
        {
            WriteStatus("NO_AUTH_CODE for packman");
            return null;
        }

        WriteStatus("AUTH_CODE_LEN=" + authCode.Length);
        var secret = GetConnectConfig("CloudPackagesKey");
        var identity = GetConnectConfig("CloudIdentity");
        if (string.IsNullOrEmpty(identity))
            identity = "https://api.unity.cn";
        if (string.IsNullOrEmpty(secret))
            secret = "6357C523886E813D1500408F05B0D7A6";

        var form = "grant_type=authorization_code&code=" + Uri.EscapeDataString(authCode)
            + "&client_id=packman&client_secret=" + Uri.EscapeDataString(secret);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded");
            using var resp = await client.PostAsync(identity.TrimEnd('/') + "/v1/oauth2/token", content);
            var body = await resp.Content.ReadAsStringAsync();
            var token = ExtractJsonString(body, "access_token");
            WriteStatus("PACKMAN_TOKEN_HTTP " + (int)resp.StatusCode + " tokenLen=" + (token?.Length ?? 0));
            return token;
        }
        catch (Exception ex)
        {
            WriteStatus("PACKMAN_TOKEN_EX " + ex.Message);
            return null;
        }
    }

    static Task<string> GetAuthCodeAsync(string clientId)
    {
        var tcs = new TaskCompletionSource<string>();
        try
        {
            Type oauthType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                oauthType = asm.GetType("UnityEditor.Connect.UnityOAuth");
                if (oauthType != null)
                    break;
            }

            if (oauthType == null)
            {
                WriteStatus("NO UnityOAuth type");
                tcs.SetResult(null);
                return tcs.Task;
            }

            var method = oauthType.GetMethod("GetAuthorizationCodeAsync", BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                WriteStatus("NO GetAuthorizationCodeAsync");
                tcs.SetResult(null);
                return tcs.Task;
            }

            var callbackType = method.GetParameters()[1].ParameterType;
            var respType = callbackType.IsGenericType ? callbackType.GetGenericArguments()[0] : callbackType;
            var generic = typeof(ImportFantasyForestOnce).GetMethod(nameof(InvokeAuth), BindingFlags.NonPublic | BindingFlags.Static);
            generic.MakeGenericMethod(respType).Invoke(null, new object[] { method, clientId, tcs });
            EditorApplication.delayCall += () =>
            {
                if (!tcs.Task.IsCompleted)
                    WriteStatus("AUTH_CODE still pending");
            };
        }
        catch (Exception ex)
        {
            WriteStatus("AUTH_CODE_EX " + ex.Message);
            tcs.TrySetResult(null);
        }

        return tcs.Task;
    }

    static void InvokeAuth<T>(MethodInfo method, string clientId, TaskCompletionSource<string> tcs)
    {
        Action<T> cb = response =>
        {
            try
            {
                var code = typeof(T).GetProperty("AuthCode")?.GetValue(response) as string;
                var ex = typeof(T).GetProperty("Exception")?.GetValue(response) as Exception;
                if (ex != null)
                    WriteStatus("AUTH_CODE_CALLBACK_EX " + ex.GetType().Name + ": " + ex.Message);
                tcs.TrySetResult(code);
            }
            catch (Exception e)
            {
                WriteStatus("AUTH_CODE_PARSE " + e.Message);
                tcs.TrySetResult(null);
            }
        };
        method.Invoke(null, new object[] { clientId, cb });
    }

    static string GetConnectConfig(string enumName)
    {
        try
        {
            Type connectType = null;
            Type enumType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                connectType ??= asm.GetType("UnityEditor.Connect.UnityConnect");
                enumType ??= asm.GetType("UnityEditor.Connect.CloudConfigUrl");
            }

            if (connectType == null || enumType == null)
            {
                WriteStatus("NO Connect config types connect=" + (connectType != null) + " enum=" + (enumType != null));
                return null;
            }

            var instance = connectType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var method = connectType.GetMethod("GetConfigurationURL", BindingFlags.Public | BindingFlags.Instance);
            if (instance == null || method == null)
                return null;
            var enumVal = Enum.Parse(enumType, enumName);
            var value = method.Invoke(instance, new[] { enumVal }) as string;
            WriteStatus("CONFIG " + enumName + " len=" + (value?.Length ?? 0));
            return value;
        }
        catch (Exception ex)
        {
            WriteStatus("CONFIG_EX " + enumName + " " + ex.Message);
            return null;
        }
    }

    static string TryConnectToken()
    {
        try
        {
            var connectType = typeof(CloudProjectSettings).Assembly.GetType("UnityEditor.Connect.UnityConnect");
            if (connectType == null)
                return null;
            var instance = connectType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (instance == null)
                return null;
            var loggedIn = connectType.GetProperty("loggedIn")?.GetValue(instance);
            WriteStatus("CONNECT loggedIn=" + loggedIn + " user=" + CloudProjectSettings.userName + " userId=" + CloudProjectSettings.userId);
            var get = connectType.GetMethod("GetAccessToken", BindingFlags.Public | BindingFlags.Instance);
            if (get == null)
                return null;
            return get.Invoke(instance, null) as string;
        }
        catch (Exception ex)
        {
            WriteStatus("CONNECT_ERROR " + ex.Message);
            return null;
        }
    }

    static async Task<(bool ok, string body, int code)> GetAsync(string url, string token)
    {
        try
        {
            using var client = NewClient(token);
            using var resp = await client.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            return ((int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300, body, (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, -1);
        }
    }

    static async Task<bool> DownloadFileAsync(string url, string destFile, string token)
    {
        try
        {
            using var client = NewClient(token);
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                WriteStatus("DOWNLOAD_HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase);
                return false;
            }

            using (var input = await resp.Content.ReadAsStreamAsync())
            using (var output = File.Create(destFile))
                await input.CopyToAsync(output);
            return true;
        }
        catch (Exception ex)
        {
            WriteStatus("DOWNLOAD_EX " + ex.Message);
            return false;
        }
    }

    static HttpClient NewClient(string token)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    static async Task<string> DownloadAndDecryptAsync(string url, string key, string token, string packageName, string publisher, string category)
    {
        var logsDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs"));
        Directory.CreateDirectory(logsDir);
        var encryptedPath = Path.Combine(logsDir, "forest-encrypted.bin");
        var decryptedPath = Path.Combine(logsDir, "FantasyForestEnvironment.unitypackage");

        if (!await DownloadFileAsync(url, encryptedPath, token) || !File.Exists(encryptedPath))
        {
            WriteStatus("HTTP download failed");
            return await DownloadWithUtilsAsync(url, key, packageName, publisher, category);
        }

        WriteStatus("ENCRYPTED size=" + new FileInfo(encryptedPath).Length);
        if (IsGzipUnityPackage(encryptedPath))
        {
            File.Copy(encryptedPath, decryptedPath, true);
            return decryptedPath;
        }

        if (TryDecryptAssetStoreFile(encryptedPath, decryptedPath, key))
            return decryptedPath;

        WriteStatus("local decrypt failed, trying AssetStoreUtils");
        return await DownloadWithUtilsAsync(url, key, packageName, publisher, category);
    }

    static bool TryDecryptAssetStoreFile(string encryptedPath, string decryptedPath, string key)
    {
        var keyBytes = TryParseKeyBytes(key);
        if (keyBytes == null)
        {
            WriteStatus("KEY_PARSE_FAIL len=" + (key?.Length ?? 0));
            return false;
        }

        WriteStatus("KEY_BYTES=" + keyBytes.Length);
        var cipher = File.ReadAllBytes(encryptedPath);
        var attempts = new List<(byte[] aesKey, byte[] iv, string name)>();
        if (keyBytes.Length >= 48)
        {
            attempts.Add((Slice(keyBytes, 0, 32), Slice(keyBytes, 32, 16), "key32+iv16"));
            attempts.Add((Slice(keyBytes, 16, 32), Slice(keyBytes, 0, 16), "iv16+key32"));
        }

        if (keyBytes.Length >= 32)
        {
            attempts.Add((Slice(keyBytes, 0, 32), cipher.Length > 16 ? Slice(cipher, 0, 16) : new byte[16], "key32+fileiv"));
            attempts.Add((Slice(keyBytes, 0, 16), Slice(keyBytes, 16, 16), "aes128-key+iv"));
        }

        foreach (var attempt in attempts)
        {
            foreach (var skip in new[] { 0, 16 })
            {
                if (cipher.Length <= skip + 32)
                    continue;
                var payload = skip == 0 ? cipher : Slice(cipher, skip, cipher.Length - skip);
                if (TryAesCbc(payload, attempt.aesKey, attempt.iv, decryptedPath))
                {
                    WriteStatus("DECRYPT_OK " + attempt.name + " skip=" + skip + " size=" + new FileInfo(decryptedPath).Length);
                    return true;
                }
            }
        }

        WriteStatus("DECRYPT_ALL_FAILED");
        return false;
    }

    static bool TryAesCbc(byte[] cipher, byte[] aesKey, byte[] iv, string dest)
    {
        try
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.KeySize = aesKey.Length * 8;
            aes.Key = aesKey;
            aes.IV = iv;
            using var decryptor = aes.CreateDecryptor();
            var plain = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
            if (plain == null || plain.Length < 64 || plain[0] != 0x1F || plain[1] != 0x8B)
                return false;
            File.WriteAllBytes(dest, plain);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static byte[] TryParseKeyBytes(string key)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        if ((key.Length % 2) == 0 && System.Text.RegularExpressions.Regex.IsMatch(key, @"\A[0-9a-fA-F]+\z"))
        {
            var bytes = new byte[key.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(key.Substring(i * 2, 2), 16);
            return bytes;
        }

        return Encoding.UTF8.GetBytes(key);
    }

    static byte[] Slice(byte[] src, int offset, int length)
    {
        var dst = new byte[length];
        Buffer.BlockCopy(src, offset, dst, 0, length);
        return dst;
    }

    static async Task<string> DownloadWithUtilsAsync(string url, string key, string packageName, string publisher, string category)
    {
        try
        {
            var type = typeof(AssetDatabase).Assembly.GetType("UnityEditor.AssetStoreUtils");
            if (type == null)
            {
                WriteStatus("NO AssetStoreUtils type");
                return null;
            }

            var instance = Activator.CreateInstance(type);
            var dest = new[] { Sanitize(publisher), Sanitize(category), Sanitize(packageName) };
            var json = "{\"download\":{\"url\":\"" + EscapeJson(url) + "\",\"key\":\"" + EscapeJson(key) + "\"}}";
            var cachePath = ResolveCachePath(type, instance, dest);
            WriteStatus("CACHE_PATH " + cachePath);

            MethodInfo download = null;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (method.Name == "Download" && method.GetParameters().Length == 7)
                    download = method;
            }

            if (download == null)
            {
                WriteStatus("NO Download(7) method");
                return null;
            }

            download.Invoke(instance, new object[] { ProductId.ToString(), url, dest, key, json, false, null });
            WriteStatus("UTILS_DOWNLOAD started");
            for (var i = 0; i < 240; i++)
            {
                if (IsGzipUnityPackage(cachePath))
                {
                    WriteStatus("UTILS_DECRYPTED size=" + new FileInfo(cachePath).Length);
                    return cachePath;
                }

                await Task.Delay(500);
            }

            WriteStatus("UTILS_DOWNLOAD timeout");
            return null;
        }
        catch (Exception ex)
        {
            WriteStatus("UTILS_EX " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    static string ResolveCachePath(Type utilsType, object instance, string[] dest)
    {
        try
        {
            var baseDir = utilsType.GetMethod("BuildBaseDownloadPath")?.Invoke(instance, new object[] { dest[0], dest[1] }) as string;
            if (!string.IsNullOrEmpty(baseDir))
            {
                var finalPath = utilsType.GetMethod("BuildFinalDownloadPath")?.Invoke(instance, new object[] { baseDir, dest[2] }) as string;
                if (!string.IsNullOrEmpty(finalPath))
                    return finalPath;
                return Path.Combine(baseDir, dest[2] + ".unitypackage");
            }
        }
        catch (Exception ex)
        {
            WriteStatus("CACHE_PATH_EX " + ex.Message);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Unity", "Asset Store-5.x", dest[0], dest[1], dest[2] + ".unitypackage");
    }

    static bool IsGzipUnityPackage(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || new FileInfo(path).Length < 64)
                return false;
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == 0x1F && fs.ReadByte() == 0x8B;
        }
        catch
        {
            return false;
        }
    }

    static string EscapeJson(string value)
    {
        return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    static bool DirectoryExistsImported()
    {
        var assets = Application.dataPath;
        foreach (var name in new[] { "Fantasy Forest Environment", "FantasyForestEnvironment", "TriForge" })
        {
            if (Directory.Exists(Path.Combine(assets, name)))
                return true;
        }

        return false;
    }

    static string ExtractJsonString(string json, string key)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
            return null;

        var marker = "\"" + key + "\"";
        var i = json.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0)
            return null;
        i += marker.Length;
        while (i < json.Length && char.IsWhiteSpace(json[i]))
            i++;
        if (i >= json.Length || json[i] != ':')
            return null;
        i++;
        while (i < json.Length && char.IsWhiteSpace(json[i]))
            i++;
        if (i >= json.Length || json[i] != '"')
            return null;
        i++;

        var sb = new StringBuilder();
        for (; i < json.Length; i++)
        {
            var c = json[i];
            if (c == '\\' && i + 1 < json.Length)
            {
                var n = json[++i];
                sb.Append(n == '/' ? '/' : n);
                continue;
            }

            if (c == '"')
                break;
            sb.Append(c);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "Unknown";
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c.ToString(), string.Empty);
        return value.Replace(".", string.Empty);
    }

    static string Trim(string s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= max ? s : s.Substring(0, max);
    }

    static void WriteStatus(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatusAbs));
            File.AppendAllText(StatusAbs, DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine);
            Debug.Log("[ImportFantasyForest] " + line);
        }
        catch
        {
            // ignored
        }
    }
}
#endif
