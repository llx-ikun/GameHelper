using System.Text;

namespace GameHelper.Core;

/// <summary>
/// 极简配置持久化。
///
/// 用「key=value」纯文本而不是 JSON：这个文件是给人看、给手改的，
/// 出问题时用记事本就能修好，比让人去猜 JSON 语法错误友好得多。
/// </summary>
public sealed class Settings
{
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public string Path { get; }

    public Settings()
    {
        Path = System.IO.Path.Combine(AppPaths.OutputDir, "设置.ini");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return;
            }

            foreach (var raw in File.ReadAllLines(Path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal))
                {
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                _map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        catch
        {
            // 配置读不出来就用默认值，不该因此起不来
        }
    }

    public string GetString(string key, string fallback) =>
        _map.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

    public int GetInt(string key, int fallback) =>
        _map.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : fallback;

    public double GetDouble(string key, double fallback) =>
        _map.TryGetValue(key, out var v) && double.TryParse(v, out var d) ? d : fallback;

    public bool GetBool(string key, bool fallback)
    {
        if (!_map.TryGetValue(key, out var v))
        {
            return fallback;
        }
        return v.Equals("1", StringComparison.OrdinalIgnoreCase)
            || v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public void Set(string key, string value) => _map[key] = value;

    public void Set(string key, bool value) => _map[key] = value ? "1" : "0";

    public void Set(string key, double value) => _map[key] = value.ToString("0.##");

    public void Set(string key, int value) => _map[key] = value.ToString();

    public void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 游戏助手 · 设置");
            sb.AppendLine("# 用记事本改完保存即可生效（下次启动时读取）。");
            sb.AppendLine();
            foreach (var kv in _map)
            {
                sb.AppendLine(kv.Key + "=" + kv.Value);
            }

            File.WriteAllText(Path, sb.ToString(), new UTF8Encoding(true));
        }
        catch
        {
            // 保存失败不影响本次使用
        }
    }
}
