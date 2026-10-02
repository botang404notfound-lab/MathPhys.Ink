using System;
using System.Collections.Generic;

namespace MathPhys.Ink.Plugin.SubjectKit;

/// <summary>本插件注册的工具 Id。</summary>
public static class SubjectKitToolIds
{
    /// <summary>学科工具（图片资料库入口）。</summary>
    public const string Id = "subjectkit";
}

/// <summary>一张学科图片：嵌入资源名、名字、教学提示。</summary>
/// <param name="Key">稳定键（分类内唯一）。</param>
/// <param name="Name">卡片上的短名。</param>
/// <param name="ResourceName">嵌入资源名（程序集清单里的全名）。</param>
/// <param name="Tip">悬停提示：这张图讲什么、适合放在哪类题旁边。</param>
public sealed record SubjectImage(string Key, string Name, string ResourceName, string Tip);

/// <summary>
/// 学科图片目录：分类 → 图片列表。
/// </summary>
/// <remarks>
/// <para>
/// ★ <b>加新图的固定三步</b>（操作卡里也这么写）：
/// ① PNG 丢进插件工程的 <c>Assets\</c>；② 在下面加一行 <c>Img(...)</c>；
/// ③ 重新构建。窗口与落画布逻辑都是通用的，一行都不用改。
/// </para>
/// <para>
/// 图片本体是<b>位图对象</b>，落画布走宿主 <c>IGfxObjectHost.AddImage</c> 通道（M22）：
/// 字节进宿主位图仓库与 .twb 的 images/ 段，存档 / 撤销 / 导出全部免费；
/// 绝不把字节塞进 GfxDraft（一帧 PNG base64 × 60 份撤销快照的老坑）。
/// </para>
/// </remarks>
public static class SubjectCatalog
{
    // ---------------------------------------------------------------- 电场线

    private static readonly IReadOnlyList<SubjectImage> EField = new[]
    {
        Img("positive", "正点电荷",
            "MathPhys.Ink.Plugin.SubjectKit.Assets.efield-positive.png",
            "正点电荷的电场线：呈放射状向四周发散，箭头指向外侧；离电荷越近线越密（场强越大）。"),
        Img("negative", "负点电荷",
            "MathPhys.Ink.Plugin.SubjectKit.Assets.efield-negative.png",
            "负点电荷的电场线：呈放射状指向电荷，箭头指向内侧；与正点电荷对比讲「出发 / 终止」。"),
        Img("dipole", "等量异种点电荷",
            "MathPhys.Ink.Plugin.SubjectKit.Assets.efield-dipole.png",
            "等量异种点电荷的电场线：从正电荷出发到负电荷终止，两电荷连线中垂面上电场方向与面垂直。"),
        Img("like-charges", "等量同种点电荷",
            "MathPhys.Ink.Plugin.SubjectKit.Assets.efield-like-charges.png",
            "等量同种（正）点电荷的电场线：两电荷之间相互「排斥」出现空区，连线中点场强为零。"),
        Img("uniform", "匀强电场",
            "MathPhys.Ink.Plugin.SubjectKit.Assets.efield-uniform.png",
            "匀强电场：平行板间电场线疏密均匀、方向一致，等势面（红线）与电场线垂直。"),
        Img("conductor", "静电平衡导体",
            "MathPhys.Ink.Plugin.SubjectKit.Assets.efield-conductor.png",
            "静电平衡状态下的导体：电荷只分布在表面，导体内部场强为零，表面附近的电场线与表面垂直。"),
    };

    /// <summary>全部分类（顺序 = 窗口左侧列表顺序）。</summary>
    public static readonly IReadOnlyList<(string Category, IReadOnlyList<SubjectImage> Images)> All
        = new (string, IReadOnlyList<SubjectImage>)[]
    {
        ("电场线", EField),
    };

    private static SubjectImage Img(string key, string name, string resource, string tip)
        => new(key, name, resource, tip);
}
