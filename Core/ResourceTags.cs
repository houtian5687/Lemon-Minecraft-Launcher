using System.Collections.Generic;

namespace App1.Core
{
    /// <summary>
    /// Modrinth 分类标签的中文映射。
    /// 未收录的标签按原样返回，保证官方新增分类时不会显示为空。
    /// </summary>
    public static class ResourceTags
    {
        private static readonly Dictionary<string, string> Names =
            new(System.StringComparer.OrdinalIgnoreCase)
            {
                // ---- 模组 ----
                ["adventure"] = "冒险",
                ["cursed"] = "诡异",
                ["decoration"] = "装饰",
                ["economy"] = "经济",
                ["equipment"] = "装备",
                ["food"] = "食物",
                ["game-mechanics"] = "游戏机制",
                ["library"] = "前置库",
                ["magic"] = "魔法",
                ["management"] = "管理",
                ["minigame"] = "小游戏",
                ["mobs"] = "生物",
                ["optimization"] = "性能优化",
                ["social"] = "社交",
                ["storage"] = "存储",
                ["technology"] = "科技",
                ["transportation"] = "运输",
                ["utility"] = "实用工具",
                ["worldgen"] = "世界生成",

                // ---- 整合包 ----
                ["challenging"] = "高难度",
                ["combat"] = "战斗",
                ["kitchen-sink"] = "综合整合",
                ["lightweight"] = "轻量",
                ["multiplayer"] = "多人联机",
                ["quests"] = "任务线",
                ["expert"] = "专家模式",
                ["skyblock"] = "空岛",

                // ---- 光影 ----
                ["atmosphere"] = "氛围",
                ["bloom"] = "泛光",
                ["cartoon"] = "卡通",
                ["cel-shaded"] = "赛璐璐",
                ["colored-lighting"] = "彩色光照",
                ["fantasy"] = "奇幻",
                ["foliage"] = "植被增强",
                ["low-end"] = "低配友好",
                ["path-tracing"] = "路径追踪",
                ["pbr"] = "PBR 材质",
                ["reflections"] = "屏幕反射",
                ["semi-realistic"] = "半写实",
                ["realistic"] = "写实",
                ["screenshots"] = "截图美化",
                ["vanilla-like"] = "原版风格",

                // ---- 材质包 ----
                ["blocks"] = "方块",
                ["gui"] = "界面",
                ["items"] = "物品",
                ["entities"] = "实体",
                ["environment"] = "环境",
                ["fonts"] = "字体",
                ["locale"] = "语言本地化",
                ["models"] = "模型",
                ["simplified"] = "简约",
                ["themed"] = "主题风格",
                ["tweaks"] = "细节微调",
                ["experimental"] = "实验性",

                // ---- 分辨率 ----
                ["8x-"] = "8x 及以下",
                ["16x"] = "16x",
                ["32x"] = "32x",
                ["64x"] = "64x",
                ["128x"] = "128x",
                ["256x"] = "256x",
                ["512x+"] = "512x 及以上",

                // ---- 加载器与平台 ----
                ["fabric"] = "Fabric",
                ["forge"] = "Forge",
                ["quilt"] = "Quilt",
                ["neoforge"] = "NeoForge",
                ["vanilla"] = "原版",
                ["bukkit"] = "Bukkit",
                ["spigot"] = "Spigot",
                ["paper"] = "Paper",
                ["purpur"] = "Purpur",
                ["iris"] = "Iris",
                ["optifine"] = "OptiFine",
                ["canvas"] = "Canvas"
            };

        /// <summary>把 Modrinth 分类标签翻译为中文，未收录时原样返回。</summary>
        public static string ToChinese(string? tag)
            => string.IsNullOrWhiteSpace(tag)
                ? tag ?? ""
                : Names.TryGetValue(tag, out var name) ? name : tag;
    }
}
