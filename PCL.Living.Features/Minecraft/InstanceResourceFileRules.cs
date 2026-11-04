using System.IO;
using System.Linq;

namespace PCL.Living.Features;

/// <summary>LF 原本地资源文件扩展名筛选，不验证包内容或改写文件名。</summary>
public static class InstanceResourceFileRules {
    // LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 InstanceResourceStore.AcceptFile。
    public static bool AcceptFile(int kind, string? fileName) {
        LegacyPathCharacters.Check(fileName);
        string extension = Path.GetExtension(fileName)!.ToLowerInvariant();
        switch (kind) {
            case 1:
                return new[] { ".png", ".jpg", ".jpeg", ".bmp" }.Contains(extension);
            case 2:
            case 3:
                return extension == ".zip";
            case 4:
                return new[] { ".litematic", ".schem", ".schematic", ".nbt" }.Contains(extension);
            default:
                return false;
        }
    }
}
