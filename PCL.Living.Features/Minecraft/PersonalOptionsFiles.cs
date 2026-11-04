using System;
using System.IO;
using System.Linq;
using System.Text;

namespace PCL.Living.Features;

/// <summary>
/// options.txt 的原始读取与合并写回。调用者管理同步锁、规则选择及会话生命周期。
/// </summary>
public static class PersonalOptionsFiles {
    public static byte[]? Read(string fileName) {
        if (!File.Exists(fileName)) return null;
        if (new FileInfo(fileName).Length > 4 * 1024 * 1024 || (File.GetAttributes(fileName) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("不读取过大或链接形式的 options.txt。");
        return File.ReadAllBytes(fileName);
    }

    /// <summary>
    /// 文本变化时保持原 UTF-8 BOM 约定，按原字节哈希保护写入，并在提交成功后清理 INI 缓存。
    /// 缓存回调异常继续传播；已成功提交的文件与备份不会回滚。
    /// </summary>
    public static bool ApplyMerged(string fileName, byte[] originalBytes, string originalText, string mergedText, Action<string> invalidateCache) {
        if (string.Equals(originalText, mergedText, StringComparison.Ordinal)) return false;
        UTF8Encoding encoded = new(originalBytes.Length >= 3 && originalBytes[0] == 0xEF && originalBytes[1] == 0xBB && originalBytes[2] == 0xBF);
        byte[] body = encoded.GetPreamble().Concat(encoded.GetBytes(mergedText)).ToArray();
        LocalGameDataStore.AtomicWrite(fileName, body, LocalGameDataStore.Hash(originalBytes), true);
        invalidateCache(fileName);
        return true;
    }
}
