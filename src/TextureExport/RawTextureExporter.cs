using System.Diagnostics;
using AssetRipper.Assets;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Export.UnityProjects.Textures;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Processing.Textures;
using AssetRipper.SourceGenerated.Classes.ClassID_213;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_89;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using Mt2Mobile.Shared;

namespace Mt2Mobile.TextureExport;

/// <summary>
/// AssetRipper 的匯出，貼圖換成 <see cref="RawTextureAssetExporter"/>。掛的位置跟 AssetRipper 自己登記 TextureAssetExporter 的三個型別一樣
///（ProjectExporter.Overrides：ITexture2D、ISprite、SpriteInformationObject），哪個資產由哪個 collection 匯出、檔名、.meta 與 GUID 都不變，
/// 只換掉寫檔那一步。第 0 步在開發機對原版各匯出一次比過：其他 73,753 個檔相同（docs/perf-battle.md）。
/// </summary>
public sealed class RawTextureExportHandler(FullConfiguration settings, TexturePlan plan) : ExportHandler(settings)
{
    public TextureExportStats Stats { get; } = new();

    protected override void BeforeExport(ProjectExporter projectExporter)
    {
        var exporter = new RawTextureAssetExporter(Settings, plan, Stats);
        projectExporter.OverrideExporter<ITexture2D>(exporter);
        projectExporter.OverrideExporter<ISprite>(exporter);
        projectExporter.OverrideExporter<SpriteInformationObject>(exporter);
    }
}

/// <summary>
/// 裝置支援的壓縮格式：寫 GetImageData()（原版的 m_ImageData，含整串 mip）包成 <see cref="RawTexture"/>，檔名照舊（.png，importer 看開頭認）。
/// 其他照舊交給 AssetRipper 解成 PNG：不支援的格式、cubemap（AssetRipper 匯出 cubemap 不翻列，第一步先不動）、
/// 開了 Read/Write 的（字型 atlas、游標，腳本要讀像素）、資料長度跟格式算出來的對不上的。
/// </summary>
public sealed class RawTextureAssetExporter(FullConfiguration configuration, TexturePlan plan, TextureExportStats stats) : TextureAssetExporter(configuration)
{
    public override bool Export(IExportContainer container, IUnityObjectBase asset, string path, FileSystem fileSystem)
    {
        var texture = (ITexture2D)asset;
        int format = (int)texture.Format_C28E;
        if (!plan.KeepsRaw(format))
        {
            stats.AddPng(null);
            return base.Export(container, asset, path, fileSystem);
        }
        string? keep = texture is ICubemap ? "cubemap" : texture.IsReadable_C28 ? "readable" : !texture.CheckAssetIntegrity() ? "missing" : null;
        byte[]? data = null;
        int mips = Math.Max(1, texture.MipCount_C28);
        if (keep == null)
        {
            data = texture.GetImageData();
            if (RawTexture.ChainLength(format, texture.Width_C28, texture.Height_C28, mips) != data.Length)
            {
                keep = "length";
                Logger.Log(LogType.Warning, LogCategory.Export, $"'{texture.Name}' 的資料 {data.Length} 位元組跟 {RawTexture.FormatName(format)} {texture.Width_C28}x{texture.Height_C28} {mips} 層算的不同，留 PNG");
            }
        }
        if (keep != null)
        {
            stats.AddPng(keep);
            return base.Export(container, asset, path, fileSystem);
        }
        uint flags = 0;
        // 同 AssetRipper 產生 .meta 的 sRGBTexture（ImporterFactory：ColorSpace 是 Linear 就是 sRGB）
        if (texture.ColorSpace_C28E == ColorSpace.Linear) flags |= RawTexture.FlagSrgb;
        if (texture.LightmapFormat_C28E == TextureUsageMode.NormalmapDXT5nm) flags |= RawTexture.FlagDxt5nm;
        var sw = Stopwatch.StartNew();
        var bytes = RawTexture.Write(format, texture.Width_C28, texture.Height_C28, mips, flags, $"{texture.Collection.Name}|{texture.PathID}", data!, TexturePlan.Lz4Level);
        stats.AddRaw(format, data!.Length, bytes.Length, sw.Elapsed.TotalSeconds);
        fileSystem.File.WriteAllBytes(path, bytes);
        return true;
    }
}
