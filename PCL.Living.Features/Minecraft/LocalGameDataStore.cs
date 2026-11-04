using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

/// <summary>
/// 本机个人游戏资料的持久化规则。调用者保留共享锁，并提供现有的资料根目录与路径检查。
/// </summary>
public sealed class LocalGameDataStore {
    private readonly Func<string> _root;
    private readonly Action<string, string> _ensureWithin;

    public LocalGameDataStore(Func<string> root, Action<string, string> ensureWithin) {
        _root = root;
        _ensureWithin = ensureWithin;
    }

    public static string GetRoot(string? testRoot) {
        if (testRoot is not null) return Path.GetFullPath(testRoot);
        string baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseFolder)) throw new IOException("无法确定当前用户的本机资料目录。");
        return Path.Combine(baseFolder, "PCL Living Fork", "GameData");
    }

    public string Address(string name) {
        string result = Path.Combine(_root(), name);
        _ensureWithin(_root(), result);
        return result;
    }

    public JObject ReadJson(string name) {
        string fileName = Address(name);
        if (!File.Exists(fileName)) return new JObject();
        if (new FileInfo(fileName).Length > 4 * 1024 * 1024) throw new IOException("本机游戏资料文件过大。");
        return JObject.Parse(File.ReadAllText(fileName, new UTF8Encoding(false, true)));
    }

    public void WriteJson(string name, JObject value) {
        string fileName = Address(name);
        AtomicWrite(fileName, new UTF8Encoding(false).GetBytes(value.ToString()), null, true);
    }

    public static string Hash(byte[] bytes) {
        using SHA256 algorithm = SHA256.Create();
        return Convert.ToBase64String(algorithm.ComputeHash(bytes));
    }

    public static void AtomicWrite(string fileName, byte[] bytes, string? expectedHash, bool backup) {
        if (File.Exists(fileName) && (File.GetAttributes(fileName) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("不修改链接形式的游戏设置文件。");
        if (backup && File.Exists(fileName + ".pcl-backup") && (File.GetAttributes(fileName + ".pcl-backup") & FileAttributes.ReparsePoint) != 0)
            throw new IOException("备份位置是链接，已停止修改游戏设置。");
        Directory.CreateDirectory(Path.GetDirectoryName(fileName)!);
        string temporary = fileName + ".pcl-write-" + Guid.NewGuid().ToString("N");
        try {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                output.Write(bytes, 0, bytes.Length);
                output.Flush(true);
            }
            if (expectedHash is not null && (!File.Exists(fileName) || Hash(File.ReadAllBytes(fileName)) != expectedHash))
                throw new IOException("游戏设置已被其他程序修改，本次同步已停止。");
            if (File.Exists(fileName)) {
                File.Replace(temporary, fileName, backup ? fileName + ".pcl-backup" : null);
            } else {
                File.Move(temporary, fileName);
            }
        } finally {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static bool ProcessMatches(int id, long startedUtc) {
        try {
            using Process value = Process.GetProcessById(id);
            return !value.HasExited && value.StartTime.ToUniversalTime().Ticks == startedUtc;
        } catch (ArgumentException) {
            return false;
        } catch {
            // 无法确定进程状态时保留会话，不尝试导入其正在写入的设置。
            return true;
        }
    }
}
