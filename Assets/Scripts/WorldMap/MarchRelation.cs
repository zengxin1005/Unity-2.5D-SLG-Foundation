/*
* 类    名：MarchRelation.cs
* 作    者：zengxin
* 创建时间：2026-08-11
*/
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 行军关系（决定箭头颜色）
/// </summary>
public enum MarchRelation
{
    Self,    // 自己
    Ally,    // 盟友/同军团
    Neutral, // 中立 / NPC
    Enemy,   // 敌人
}

/// <summary>
/// 行军实体数据接口（数据层定义，可替换为项目实际数据结构）
/// </summary>
public interface IMarchEntity
{
    /// <summary>行军唯一ID</summary>
    long Id { get; }

    /// <summary>起点瓦片X</summary>
    int StartTileX { get; }
    /// <summary>起点瓦片Y</summary>
    int StartTileY { get; }

    /// <summary>终点瓦片X</summary>
    int EndTileX { get; }
    /// <summary>终点瓦片Y</summary>
    int EndTileY { get; }

    /// <summary>
    /// 中间路点瓦片坐标（可选：null/空 = 直线路径，向后兼容）。
    /// 采样与渲染按 [起点, 路点..., 终点] 组装折线，t 为累计弧长。
    ///
    /// 使用契约（地图路径接入）：
    /// 1. 坐标系 = 瓦片坐标（与 StartTileX/EndTileX 同系，非 content 像素）。
    /// 2. 只填中间途经点，不含起点/终点（框架自动补首尾）。
    /// 3. 按下标顺序 = 行军行经顺序。
    /// 4. 任一途经点越出地图边界 → 整条行军被拒 + LogWarning（WorldObjectManager.AddMarch 校验）。
    /// 5. 建议只传"拐点"：同一直线上的中间点抽稀去掉（形状不变、段数大减），
    ///    勿直接填寻路逐格路径——短段会让箭头过密重叠、段数爆炸。
    /// </summary>
    IReadOnlyList<Vector2Int> Waypoints { get; }

    /// <summary>
    /// Content 像素坐标系下的移动速度（像素/秒）
    /// </summary>
    float Speed { get; }

    /// <summary>与当前玩家的关系</summary>
    MarchRelation Relation { get; }
}

/// <summary>
/// 默认数据结构，可用于测试或作为数据层 DTO
/// </summary>
public struct MarchEntity : IMarchEntity
{
    public long Id { get; set; }
    public int StartTileX { get; set; }
    public int StartTileY { get; set; }
    public int EndTileX { get; set; }
    public int EndTileY { get; set; }
    public float Speed { get; set; }
    public MarchRelation Relation { get; set; }
    public IReadOnlyList<Vector2Int> Waypoints { get; set; }
}
