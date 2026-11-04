using System;
using System.IO;
using System.Text;

namespace PCL.Living.Features;

/// <summary>
/// 从 level.dat 的 Data 标签读取的本地存档元数据。
/// </summary>
public sealed class WorldMetadataInfo {
    public string? Name { get; set; }
    public DateTime LastPlayedUtc { get; set; }
    public string? VersionName { get; set; }
    public int? DataVersion { get; set; }
    public int? GameMode { get; set; }
    public int? Difficulty { get; set; }
    public bool? Hardcore { get; set; }
    public bool? AllowCommands { get; set; }
    public bool? WasModded { get; set; }
    public bool? Snapshot { get; set; }
    public long? Seed { get; set; }
    public int? SpawnX { get; set; }
    public int? SpawnY { get; set; }
    public int? SpawnZ { get; set; }
    public long? DayTime { get; set; }
    public long? Time { get; set; }
}

/// <summary>
/// 读取已经解压的 NBT 流，不管理文件路径、压缩或输入流的生命周期。
/// </summary>
public static class WorldMetadataReader {
    /// <summary>
    /// 基础读取在取得名称与最后游玩时间后结束；详情读取继续读取整个 Data 标签。
    /// </summary>
    /// <param name="input">位于 NBT 根标签起始处的流；读取后保持打开。</param>
    /// <param name="details">是否读取详情字段，并允许返回没有最后游玩时间的旧存档。</param>
    /// <returns>没有 Data 复合标签，或基础读取没有有效最后游玩时间时返回 null。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> 为 null。</exception>
    public static WorldMetadataInfo? Read(Stream input, bool details) {
        if (input is null) throw new ArgumentNullException(nameof(input));
        return new Parser(input).Read(details);
    }

    private sealed class Parser {
        private readonly Stream _input;
        private readonly WorldMetadataInfo _result = new();
        private int _budget = 16 * 1024 * 1024;

        internal Parser(Stream input) {
            _input = input;
        }

        internal WorldMetadataInfo? Read(bool details) {
            if (ByteValue() != 10) return null;
            StringValue();
            while (true) {
                int kind = ByteValue();
                if (kind == 0) break;
                string key = StringValue();
                if (kind == 10 && key == "Data") {
                    ReadData(details);
                    return !details && _result.LastPlayedUtc == DateTime.MinValue ? null : _result;
                }
                Skip(kind, 0);
            }
            return null;
        }

        private void ReadData(bool details) {
            while (true) {
                int kind = ByteValue();
                if (kind == 0) return;
                string key = StringValue();
                if (key == "LevelName" && kind == 8) {
                    _result.Name = StringValue();
                } else if (key == "LastPlayed" && kind == 4) {
                    long timestamp = LongValue();
                    if (timestamp > 0) _result.LastPlayedUtc = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime;
                } else if (details && kind == 3 && (key == "GameType" || key == "Difficulty" || key == "DataVersion" || key == "SpawnX" || key == "SpawnY" || key == "SpawnZ")) {
                    int number = IntValue();
                    switch (key) {
                    case "GameType": _result.GameMode = number; break;
                    case "Difficulty": _result.Difficulty = number; break;
                    case "DataVersion": _result.DataVersion = number; break;
                    case "SpawnX": _result.SpawnX = number; break;
                    case "SpawnY": _result.SpawnY = number; break;
                    case "SpawnZ": _result.SpawnZ = number; break;
                    }
                } else if (details && kind == 1 && (key == "hardcore" || key == "allowCommands" || key == "WasModded" || key == "Difficulty")) {
                    int number = ByteValue();
                    switch (key) {
                    case "hardcore": _result.Hardcore = number != 0; break;
                    case "allowCommands": _result.AllowCommands = number != 0; break;
                    case "WasModded": _result.WasModded = number != 0; break;
                    case "Difficulty": _result.Difficulty = number; break;
                    }
                } else if (details && kind == 4 && (key == "RandomSeed" || key == "Time" || key == "DayTime")) {
                    long number = LongValue();
                    switch (key) {
                    case "RandomSeed": if (!_result.Seed.HasValue) _result.Seed = number; break;
                    case "Time": _result.Time = number; break;
                    case "DayTime": _result.DayTime = number; break;
                    }
                } else if (details && kind == 10 && (key == "Version" || key == "WorldGenSettings")) {
                    ReadExtra(key);
                } else {
                    Skip(kind, 0);
                }
                if (!details && _result.Name is not null && _result.LastPlayedUtc != DateTime.MinValue) return;
            }
        }

        private void ReadExtra(string section) {
            while (true) {
                int kind = ByteValue();
                if (kind == 0) return;
                string key = StringValue();
                if (section == "Version" && key == "Name" && kind == 8) {
                    _result.VersionName = StringValue();
                } else if (section == "Version" && key == "Snapshot" && kind == 1) {
                    _result.Snapshot = ByteValue() != 0;
                } else if (section == "WorldGenSettings" && key == "seed" && kind == 4) {
                    _result.Seed = LongValue();
                } else {
                    Skip(kind, 1);
                }
            }
        }

        private int ByteValue() {
            _budget -= 1;
            if (_budget < 0) throw new InvalidDataException("存档元数据超过读取限制。");
            int value = _input.ReadByte();
            if (value < 0) throw new EndOfStreamException();
            return value;
        }

        private int IntValue() {
            uint value = 0;
            for (int index = 0; index < 4; index++) {
                value = (value << 8) | (uint) ByteValue();
            }
            return unchecked((int) value);
        }

        private long LongValue() {
            ulong value = 0;
            for (int index = 0; index < 8; index++) {
                value = (value << 8) | (byte) ByteValue();
            }
            return unchecked((long) value);
        }

        private string StringValue() {
            int length = (ByteValue() << 8) | ByteValue();
            byte[] bytes = new byte[length];
            for (int index = 0; index < length; index++) {
                bytes[index] = (byte) ByteValue();
            }
            return length == 0 ? "" : Encoding.UTF8.GetString(bytes);
        }

        private void Discard(long count) {
            if (count < 0 || count > _budget) throw new InvalidDataException("存档元数据长度无效。");
            for (long index = 1; index <= count; index++) {
                ByteValue();
            }
        }

        private void Skip(int kind, int depth) {
            if (depth > 64) throw new InvalidDataException("存档元数据层级过深。");
            switch (kind) {
            case 0: return;
            case 1: Discard(1); break;
            case 2: Discard(2); break;
            case 3:
            case 5: Discard(4); break;
            case 4:
            case 6: Discard(8); break;
            case 7: Discard(IntValue()); break;
            case 8: StringValue(); break;
            case 9:
                int element = ByteValue();
                int count = IntValue();
                if (count < 0 || count > 100000) throw new InvalidDataException("存档元数据列表长度无效。");
                for (int index = 1; index <= count; index++) {
                    Skip(element, depth + 1);
                }
                break;
            case 10:
                while (true) {
                    int child = ByteValue();
                    if (child == 0) break;
                    StringValue();
                    Skip(child, depth + 1);
                }
                break;
            case 11: Discard((long) IntValue() * 4); break;
            case 12: Discard((long) IntValue() * 8); break;
            default: throw new InvalidDataException("未知的存档元数据类型。");
            }
        }
    }
}