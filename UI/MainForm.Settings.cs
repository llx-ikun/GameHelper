namespace GameHelper.UI;

/// <summary>把用户上一次的设置读回来、关窗口时存回去。</summary>
internal sealed partial class MainForm
{
    private void ApplySettings()
    {
        double warn = _settings.GetDouble("警告阈值", DefaultWarnPct);
        double crit = _settings.GetDouble("危急阈值", DefaultCritPct);

        warn = Math.Clamp(warn, (double)_numWarn.Minimum, (double)_numWarn.Maximum);
        crit = Math.Clamp(crit, (double)_numCrit.Minimum, (double)_numCrit.Maximum);
        if (warn >= crit)
        {
            // 配置被手改成非法值时，退回默认，而不是启动就报错。
            warn = DefaultWarnPct;
            crit = DefaultCritPct;
        }

        _numWarn.Value = (decimal)warn;
        _numCrit.Value = (decimal)crit;

        _monitor.WarnPct = warn;
        _monitor.CritPct = crit;
        _bar.WarnPct = warn;
        _bar.CritPct = crit;
        _chart.WarnPct = warn;
        _chart.CritPct = crit;

        _chkThrottle.Checked = _settings.GetBool("记录降频原因", false);
        _chkOnlyOther.Checked = _settings.GetBool("仅显示后台程序", false);
        _chkPrimaryOnly.Checked = _settings.GetBool("只显示游戏显卡", true);

        // 显存提醒默认开。quiet=true：状态照同步，但不写日志 ——
        // 每次开程序都先蹦一行「已开启显存提醒」纯属噪音。
        SetVramNotify(_settings.GetBool("使用显存提醒", true), quiet: true);

        if (!_settings.GetBool("显示日志", true))
        {
            SetLogVisible(false);
        }

        AddLog("已载入设置：" + _settings.Path);
    }

    private void SaveSettings()
    {
        _settings.Set("警告阈值", (double)_numWarn.Value);
        _settings.Set("危急阈值", (double)_numCrit.Value);
        _settings.Set("记录降频原因", _chkThrottle.Checked);
        _settings.Set("使用显存提醒", _notifier.Enabled);
        _settings.Set("仅显示后台程序", _chkOnlyOther.Checked);
        _settings.Set("只显示游戏显卡", _chkPrimaryOnly.Checked);
        _settings.Set("显示日志", _logVisible);
        _settings.Set("游戏目录", _txtGameDir.Text.Trim());
        _settings.Set("备份目录", _txtBackupRoot.Text.Trim());
        _settings.Save();
    }
}
