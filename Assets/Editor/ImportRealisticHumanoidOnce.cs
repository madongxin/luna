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
/// One-shot importer for RPG Character Mecanim Animation Pack FREE (65284).
/// Hooks the Humanoid dummy + locomotion clips onto Starter Assets third person.
/// </summary>
[InitializeOnLoad]
public static class ImportRealisticHumanoidOnce
{
    static readonly long[] ProductIds = { 65284 };
    const string StatusAbs = @"C:\Users\dongx\FirstFPS\Logs\import-realistic-humanoid-status.txt";
    const string PrefsImported = "FirstFPS.ImportRealisticHumanoid.Imported";
    const string SessionKey = "FirstFPS.ImportRealisticHumanoid.Running";

    static readonly string[] ApiHosts =
    {
        "https://packages-v2.unity.cn",
        "https://packages-v2.unity.com"
    };

    static ImportRealisticHumanoidOnce()
    {
        WriteStatus("CTOR product=65284");
        if (EditorPrefs.GetBool(PrefsImported, false) || DirectoryExistsImported())
        {
            EditorPrefs.SetBool(PrefsImported, true);
            EditorApplication.delayCall += BindIfPrefabMissing;
            return;
        }

        SessionState.SetBool(SessionKey, false);
        EditorApplication.delayCall += Kickoff;
    }

    [MenuItem("GameMesh/Import Realistic Humanoid Locomotion Pack")]
    public static void ImportMenu()
    {
        SessionState.SetBool(SessionKey, false);
        EditorPrefs.SetBool(PrefsImported, false);
        _ = RunAsync();
    }

    static void Kickoff()
    {
        if (EditorPrefs.GetBool(PrefsImported, false) || DirectoryExistsImported())
        {
            EditorPrefs.SetBool(PrefsImported, true);
            WriteStatus("ALREADY_IMPORTED");
            BindIfPrefabMissing();
            return;
        }

        _ = RunAsync();
    }

    static async Task RunAsync()
    {
        if (SessionState.GetBool(SessionKey, false))
            return;
        SessionState.SetBool(SessionKey, true);

        try
        {
            ForceBothInput();
            for (var i = 0; i < 40 && (EditorApplication.isCompiling || EditorApplication.isUpdating); i++)
                await Task.Delay(500);

            WriteStatus("START user=" + CloudProjectSettings.userName);
            var token = await GetTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                WriteStatus("FAIL no packman token. Sign in via Editor account icon");
                SessionState.SetBool(SessionKey, false);
                return;
            }

            WriteStatus("TOKEN_LEN=" + token.Length);
            string downloadJson = null;
            long usedId = 0;
            foreach (var productId in ProductIds)
            {
                foreach (var host in ApiHosts)
                {
                    var (ok, body, code) = await GetAsync(host + "/-/api/legacy-package-download-info/" + productId, token);
                    WriteStatus("DOWNLOAD_INFO id=" + productId + " host=" + host + " http=" + code +
                                " bytes=" + (body?.Length ?? 0));
                    if (ok && body != null && body.Contains("\"url\""))
                    {
                        downloadJson = body;
                        usedId = productId;
                        break;
                    }
                }

                if (downloadJson != null)
                    break;
            }

            if (downloadJson == null)
            {
                WriteStatus("FAIL add RPG Character Mecanim Animation Pack FREE (65284) to My Assets first");
                SessionState.SetBool(SessionKey, false);
                return;
            }

            var url = ExtractJsonString(downloadJson, "url");
            var key = ExtractJsonString(downloadJson, "key");
            var packageName = ExtractJsonString(downloadJson, "filename_safe_package_name") ?? "RealisticHumanoid";
            if (string.IsNullOrEmpty(url))
            {
                WriteStatus("FAIL parse url");
                SessionState.SetBool(SessionKey, false);
                return;
            }

            WriteStatus("DOWNLOADING id=" + usedId + " " + packageName + " keyLen=" + (key?.Length ?? 0));
            var destFile = await DownloadAndDecryptAsync(url, key, token);
            if (string.IsNullOrEmpty(destFile) || !File.Exists(destFile))
            {
                WriteStatus("FAIL download/decrypt");
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
            SessionState.SetBool(SessionKey, false);
        }
    }

    static void OnImported(string packageName)
    {
        AssetDatabase.importPackageCompleted -= OnImported;
        AssetDatabase.importPackageFailed -= OnImportFailed;
        EditorPrefs.SetBool(PrefsImported, true);
        ForceBothInput();
        WriteStatus("OK imported " + packageName);
        SessionState.SetBool(SessionKey, false);
        EditorApplication.delayCall += BindPlayerSafe;
    }

    static void BindIfPrefabMissing()
    {
        var prefab = Path.Combine(Application.dataPath, "GameMesh", "Prefabs", "PlayerHumanoid.prefab");
        if (File.Exists(prefab))
            return;
        BindPlayerSafe();
    }

    static void BindPlayerSafe()
    {
        var type = Type.GetType("GameMesh.Editor.StoreHumanoidPlayerBuilder, GameMesh.Editor");
        type?.GetMethod("BindAfterImport", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        WriteStatus("BIND invoked type=" + (type != null));
    }

    static void OnImportFailed(string packageName, string error)
    {
        AssetDatabase.importPackageCompleted -= OnImported;
        AssetDatabase.importPackageFailed -= OnImportFailed;
        WriteStatus("FAIL import " + packageName + ": " + error);
        SessionState.SetBool(SessionKey, false);
    }

    static async Task<string> DownloadAndDecryptAsync(string url, string key, string token)
    {
        var logsDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs"));
        Directory.CreateDirectory(logsDir);
            var encryptedPath = Path.Combine(logsDir, "realistic-humanoid-encrypted.bin");
            var decryptedPath = Path.Combine(logsDir, "RealisticHumanoid.unitypackage");

        if (File.Exists(decryptedPath) && new FileInfo(decryptedPath).Length > 1024 * 1024 && IsGzip(decryptedPath))
        {
            WriteStatus("REUSE " + decryptedPath);
            return decryptedPath;
        }

        if (!await DownloadFileAsync(url, encryptedPath, token) || !File.Exists(encryptedPath))
            return null;

        WriteStatus("ENCRYPTED size=" + new FileInfo(encryptedPath).Length);
        if (IsGzip(encryptedPath))
        {
            File.Copy(encryptedPath, decryptedPath, true);
            return decryptedPath;
        }

        if (TryDecryptStreaming(encryptedPath, decryptedPath, key))
        {
            try { File.Delete(encryptedPath); } catch { /* ignore */ }
            return decryptedPath;
        }

        WriteStatus("DECRYPT_ALL_FAILED");
        return null;
    }

    static bool TryDecryptStreaming(string encryptedPath, string decryptedPath, string key)
    {
        var keyBytes = TryParseKeyBytes(key);
        if (keyBytes == null)
        {
            WriteStatus("KEY_PARSE_FAIL len=" + (key?.Length ?? 0));
            return false;
        }

        WriteStatus("KEY_BYTES=" + keyBytes.Length);
        var attempts = new List<(byte[] aesKey, byte[] iv, int skip, string name)>();
        if (keyBytes.Length >= 48)
        {
            attempts.Add((Slice(keyBytes, 0, 32), Slice(keyBytes, 32, 16), 0, "key32+iv16"));
            attempts.Add((Slice(keyBytes, 16, 32), Slice(keyBytes, 0, 16), 0, "iv16+key32"));
        }

        if (keyBytes.Length >= 32)
        {
            attempts.Add((Slice(keyBytes, 0, 16), Slice(keyBytes, 16, 16), 0, "aes128"));
            attempts.Add((Slice(keyBytes, 0, 32), Slice(keyBytes, 0, 16), 16, "key32+fileiv"));
        }

        foreach (var attempt in attempts)
        {
            if (TryAesCbcFile(encryptedPath, decryptedPath, attempt.aesKey, attempt.iv, attempt.skip))
            {
                WriteStatus("DECRYPT_OK " + attempt.name + " size=" + new FileInfo(decryptedPath).Length);
                return true;
            }
        }

        return false;
    }

    static bool TryAesCbcFile(string src, string dest, byte[] aesKey, byte[] iv, int skip)
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
            using var input = File.OpenRead(src);
            if (skip > 0)
                input.Position = skip;
            using var crypto = new CryptoStream(input, decryptor, CryptoStreamMode.Read);
            var header = new byte[2];
            if (crypto.Read(header, 0, 2) != 2 || header[0] != 0x1F || header[1] != 0x8B)
                return false;

            var tmp = dest + ".tmp";
            using (var output = File.Create(tmp))
            {
                output.Write(header, 0, 2);
                crypto.CopyTo(output);
            }

            if (File.Exists(dest))
                File.Delete(dest);
            File.Move(tmp, dest);
            return true;
        }
        catch
        {
            try { File.Delete(dest + ".tmp"); } catch { /* ignore */ }
            return false;
        }
    }

    static async Task<string> GetTokenAsync()
    {
        var authTask = GetAuthCodeAsync("packman");
        var finished = await Task.WhenAny(authTask, Task.Delay(45000));
        if (finished != authTask)
            return null;
        var authCode = await authTask;
        if (string.IsNullOrEmpty(authCode))
            return null;

        var secret = GetConnectConfig("CloudPackagesKey") ?? "6357C523886E813D1500408F05B0D7A6";
        var identity = GetConnectConfig("CloudIdentity") ?? "https://api.unity.cn";
        var form = "grant_type=authorization_code&code=" + Uri.EscapeDataString(authCode)
            + "&client_id=packman&client_secret=" + Uri.EscapeDataString(secret);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded");
        using var resp = await client.PostAsync(identity.TrimEnd('/') + "/v1/oauth2/token", content);
        var body = await resp.Content.ReadAsStringAsync();
        var token = ExtractJsonString(body, "access_token");
        WriteStatus("PACKMAN_TOKEN_HTTP " + (int)resp.StatusCode + " tokenLen=" + (token?.Length ?? 0));
        return token;
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
                tcs.SetResult(null);
                return tcs.Task;
            }

            var method = oauthType.GetMethod("GetAuthorizationCodeAsync", BindingFlags.Public | BindingFlags.Static);
            var callbackType = method.GetParameters()[1].ParameterType;
            var respType = callbackType.IsGenericType ? callbackType.GetGenericArguments()[0] : callbackType;
            typeof(ImportRealisticHumanoidOnce).GetMethod(nameof(InvokeAuth), BindingFlags.NonPublic | BindingFlags.Static)
                .MakeGenericMethod(respType)
                .Invoke(null, new object[] { method, clientId, tcs });
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
            var code = typeof(T).GetProperty("AuthCode")?.GetValue(response) as string;
            tcs.TrySetResult(code);
        };
        method.Invoke(null, new object[] { clientId, cb });
    }

    static string GetConnectConfig(string enumName)
    {
        Type connectType = null;
        Type enumType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            connectType ??= asm.GetType("UnityEditor.Connect.UnityConnect");
            enumType ??= asm.GetType("UnityEditor.Connect.CloudConfigUrl");
        }

        var instance = connectType?.GetProperty("instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var method = connectType?.GetMethod("GetConfigurationURL", BindingFlags.Public | BindingFlags.Instance);
        if (instance == null || method == null || enumType == null)
            return null;
        return method.Invoke(instance, new[] { Enum.Parse(enumType, enumName) }) as string;
    }

    static async Task<(bool ok, string body, int code)> GetAsync(string url, string token)
    {
        using var client = NewClient(token, TimeSpan.FromSeconds(30));
        using var resp = await client.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();
        return ((int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300, body, (int)resp.StatusCode);
    }

    static async Task<bool> DownloadFileAsync(string url, string destFile, string token)
    {
        try
        {
            using var client = NewClient(token, TimeSpan.FromMinutes(30));
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                WriteStatus("DOWNLOAD_HTTP " + (int)resp.StatusCode);
                return false;
            }

            var total = resp.Content.Headers.ContentLength ?? -1;
            WriteStatus("DOWNLOAD start total=" + total);
            using var input = await resp.Content.ReadAsStreamAsync();
            using var output = File.Create(destFile);
            var buffer = new byte[1024 * 1024];
            long read = 0;
            long lastLog = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, n);
                read += n;
                if (read - lastLog >= 8L * 1024 * 1024)
                {
                    lastLog = read;
                    WriteStatus("DOWNLOAD " + (read / (1024 * 1024)) + "MB / " +
                                (total > 0 ? (total / (1024 * 1024)).ToString() : "?") + "MB");
                }
            }

            WriteStatus("DOWNLOAD done " + (read / (1024 * 1024)) + "MB");
            return read > 1024;
        }
        catch (Exception ex)
        {
            WriteStatus("DOWNLOAD_EX " + ex.Message);
            return false;
        }
    }

    static HttpClient NewClient(string token, TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    static bool DirectoryExistsImported()
    {
        var assets = Application.dataPath;
        string[] names =
        {
            "ExplosiveLLC",
            "RPG Character Mecanim Animation Pack FREE",
            "RPGCharacterMecanimAnimationPackFREE",
            "Real Human",
            "RealHuman",
            "Pyramis Arts"
        };
        for (var i = 0; i < names.Length; i++)
        {
            if (Directory.Exists(Path.Combine(assets, names[i])))
                return true;
        }

        try
        {
            foreach (var dir in Directory.GetDirectories(assets))
            {
                var name = Path.GetFileName(dir);
                if (name.IndexOf("Explosive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("RPG Character", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Real Human", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }

    static void ForceBothInput()
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ProjectSettings", "ProjectSettings.asset"));
            if (!File.Exists(path))
                return;
            var text = File.ReadAllText(path);
            var patched = System.Text.RegularExpressions.Regex.Replace(text, @"activeInputHandler:\s*\d+", "activeInputHandler: 2");
            if (patched != text)
            {
                File.WriteAllText(path, patched);
                WriteStatus("INPUT_HANDLER set Both");
            }
        }
        catch (Exception ex)
        {
            WriteStatus("INPUT_HANDLER_EX " + ex.Message);
        }
    }

    static bool IsGzip(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 64)
                return false;
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == 0x1F && fs.ReadByte() == 0x8B;
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

    static string ExtractJsonString(string json, string key)
    {
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

    static void WriteStatus(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatusAbs));
            File.AppendAllText(StatusAbs, DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine);
            Debug.Log("[ImportHumanoid] " + line);
        }
        catch
        {
            // ignored
        }
    }
}
#endif
