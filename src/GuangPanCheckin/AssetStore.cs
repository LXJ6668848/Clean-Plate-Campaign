using System.Drawing;

namespace GuangPanCheckin;

/// <summary>
/// 打卡示意图资产管理：把图复制进 AppData、校验格式与体积、按需加载。
/// 复制而不是引用原路径，原图被移动/删除也不影响。
/// </summary>
public static class AssetStore
{
    /// <summary>用户指定的初始图片（只在首次自动导入时使用）。</summary>
    public const string DefaultSource = @"C:\Users\admin\Pictures\20614752_112749214000_2.jpg";

    private const long MaxBytes = 50L * 1024 * 1024;
    private const int MaxSide = 8000;

    private static readonly string[] Allowed = { ".jpg", ".jpeg", ".png", ".bmp" };

    public static string Dir => Path.Combine(Store.Dir, "assets");

    /// <summary>返回当前可用的图片路径；一个都没有时返回空串。</summary>
    public static string Resolve()
    {
        var configured = Store.Settings.TargetImagePath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var asset = FindAsset();
        if (asset is not null) return asset;

        // 首次使用：把 Pictures 里的原图复制进来
        if (File.Exists(DefaultSource))
        {
            var (ok, result) = ImportTo(DefaultSource, Dir);
            if (ok)
            {
                Store.Settings.TargetImagePath = result;
                Store.SaveSettings();
                AppLog.Info("已复制打卡示意图: " + result);
                return result;
            }
            AppLog.Warn("复制默认示意图失败: " + result);
        }

        return "";
    }

    public static string? FindAsset()
    {
        try
        {
            if (!Directory.Exists(Dir)) return null;
            return Directory.GetFiles(Dir, "target.*")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>加载当前示意图。图片会复制成独立 Bitmap，不占用文件句柄。</summary>
    public static (Image? img, string err) Load()
    {
        var path = Resolve();
        if (path.Length == 0) return (null, "还没设置打卡示意图，可在设置里选一张");
        return LoadFrom(path);
    }

    public static (Image? img, string err) LoadFrom(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return (null, "图片文件不存在，可能被移动或删除了");

            var len = new FileInfo(path).Length;
            if (len > MaxBytes)
                return (null, $"图片太大（{len / 1024 / 1024} MB），换一张小一点的吧");
            if (len == 0) return (null, "图片文件是空的");

            // FileShare.ReadWrite：别的程序正开着这张图也能读
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var src = Image.FromStream(fs, false, false);

            if (src.Width > MaxSide || src.Height > MaxSide)
                return (null, $"图片尺寸太大（{src.Width}×{src.Height}），换一张小一点的吧");

            var copy = new Bitmap(src);
            return (copy, "");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取图片失败 {path}: {ex.Message}");
            return (null, "图片打不开，文件可能已损坏或不是图片格式");
        }
    }

    /// <summary>把图片复制进目标目录，返回新路径。不做设置写入，便于自检时指向临时目录。</summary>
    public static (bool ok, string msg) ImportTo(string src, string destDir)
    {
        try
        {
            if (!File.Exists(src)) return (false, "选择的文件不存在");

            var ext = Path.GetExtension(src).ToLowerInvariant();
            if (!Allowed.Contains(ext)) return (false, "只支持 jpg / png / bmp 格式");

            var (probe, err) = LoadFrom(src);
            if (probe is null) return (false, err);
            probe.Dispose();

            Directory.CreateDirectory(destDir);
            foreach (var old in Directory.GetFiles(destDir, "target.*"))
            {
                try { File.Delete(old); } catch { }
            }

            var dest = Path.Combine(destDir, "target" + ext);
            File.Copy(src, dest, true);
            return (true, dest);
        }
        catch (Exception ex)
        {
            return (false, "复制失败：" + ex.Message);
        }
    }

    /// <summary>设置页换图：校验后复制并写回配置。</summary>
    public static (bool ok, string msg) Import(string src)
    {
        var (ok, result) = ImportTo(src, Dir);
        if (!ok) return (false, result);

        Store.Settings.TargetImagePath = result;
        Store.SaveSettings();
        AppLog.Info("更换打卡示意图: " + result);
        return (true, result);
    }

    /// <summary>给设置页显示的一行状态文案。</summary>
    public static string Describe()
    {
        var path = Resolve();
        if (path.Length == 0) return "未设置（点「更换…」选一张）";
        var name = Path.GetFileName(path);
        return File.Exists(path) ? $"已设置 · {name}" : $"文件缺失 · {name}";
    }
}
