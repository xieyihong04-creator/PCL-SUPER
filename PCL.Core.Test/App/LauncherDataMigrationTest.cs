using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;

namespace PCL.Core.Test.App;

[TestClass]
public class LauncherDataMigrationTest
{
    private string _root = null!;
    private string _target = null!;
    private string _marker = null!;
    private string _legacy = null!;
    private string _legacyOld = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLSuperMigrationTest_" + Guid.NewGuid().ToString("N"));
        _target = Path.Combine(_root, "PCLSUPER");
        _marker = Path.Combine(_target, ".migrated-from-pclce");
        _legacy = Path.Combine(_root, "PCLCE");
        _legacyOld = Path.Combine(_root, ".PCLCE");
        Directory.CreateDirectory(_target);
        Directory.CreateDirectory(_legacy);
        Directory.CreateDirectory(_legacyOld);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    [TestMethod]
    public void CopiesSharedConfigKeyAndProfiles()
    {
        Write(_legacy, "config.v1.json", "cfg");
        Write(_legacy, "UserKey.bin", "key");
        Write(_legacyOld, "profiles.json", "profiles");
        Write(_legacy, "config.v1.json.tmp1234", "junk");
        Write(_legacy, "random.txt", "junk");

        var result = LauncherDataMigration.MigrateSharedConfig(_target, _marker, [_legacy, _legacyOld]);

        Assert.IsTrue(result.Ran);
        CollectionAssert.AreEquivalent(
            new[] { "config.v1.json", "UserKey.bin", "profiles.json" },
            result.CopiedFileNames.ToArray());
        Assert.AreEqual("cfg", Read(_target, "config.v1.json"));
        Assert.AreEqual("key", Read(_target, "UserKey.bin"));
        Assert.AreEqual("profiles", Read(_target, "profiles.json"));
        Assert.IsFalse(File.Exists(Path.Combine(_target, "config.v1.json.tmp1234")));
        Assert.IsFalse(File.Exists(Path.Combine(_target, "random.txt")));
        Assert.IsTrue(File.Exists(_marker), "迁移完成后应写入标记，避免重复导入");
        Assert.IsTrue(File.Exists(Path.Combine(_legacy, "config.v1.json")), "不得移动或删除原目录内容");
    }

    [TestMethod]
    public void DoesNotOverwriteExistingTargetFiles()
    {
        Write(_target, "config.v1.json", "new");
        Write(_legacy, "config.v1.json", "old");
        Write(_legacy, "UserKey.bin", "key");

        var result = LauncherDataMigration.MigrateSharedConfig(_target, _marker, [_legacy]);

        Assert.IsTrue(result.Ran);
        Assert.AreEqual("new", Read(_target, "config.v1.json"));
        Assert.IsFalse(result.CopiedFileNames.Contains("config.v1.json"));
        Assert.IsTrue(result.CopiedFileNames.Contains("UserKey.bin"));
    }

    [TestMethod]
    public void SkipsWhenAlreadyMigrated()
    {
        Write(_legacy, "config.v1.json", "cfg");
        File.WriteAllText(_marker, "done");

        var result = LauncherDataMigration.MigrateSharedConfig(_target, _marker, [_legacy]);

        Assert.IsFalse(result.Ran);
        Assert.AreEqual(0, result.CopiedFileNames.Count);
        Assert.IsFalse(File.Exists(Path.Combine(_target, "config.v1.json")));
    }

    [TestMethod]
    public void IgnoresMissingSourceDirectories()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        var result = LauncherDataMigration.MigrateSharedConfig(_target, _marker, [missing]);

        Assert.IsFalse(result.Ran);
        Assert.AreEqual(0, result.CopiedFileNames.Count);
        Assert.IsFalse(File.Exists(_marker), "没有可迁移来源时不应写入标记");
    }

    [TestMethod]
    public void NeverMigratesIntoItself()
    {
        Write(_target, "config.v1.json", "cfg");

        var result = LauncherDataMigration.MigrateSharedConfig(_target, _marker, [_target + Path.DirectorySeparatorChar]);

        Assert.IsFalse(result.Ran);
        Assert.IsFalse(File.Exists(_marker));
    }

    private static void Write(string directory, string name, string content) =>
        File.WriteAllText(Path.Combine(directory, name), content);

    private static string Read(string directory, string name) =>
        File.ReadAllText(Path.Combine(directory, name));
}
