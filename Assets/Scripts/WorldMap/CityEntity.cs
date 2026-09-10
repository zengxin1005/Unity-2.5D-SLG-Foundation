/*
* 类    名：CityEntity.cs
* 作    者：zengxin
* 创建时间：2026-8-20
*/
/// <summary>
/// 城池实体数据接口与默认数据结构（数据层定义，可替换为项目实际数据结构）。
/// 业务侧自行增删：WorldObjectManager.AddCity / RemoveCity
/// </summary>
public interface ICityEntity
{
    /// <summary>城池唯一ID</summary>
    long Id { get; }

    /// <summary>所在瓦片X（无边缘）</summary>
    int TileX { get; }

    /// <summary>所在瓦片Y（无边缘）</summary>
    int TileY { get; }

    /// <summary>城池类型（决定精灵索引，见 WorldObjectManager._citySprites）</summary>
    int Type { get; }

    /// <summary>与当前玩家的关系（预留：决定名称颜色/势力标识等）</summary>
    MarchRelation Relation { get; }
}

/// <summary>
/// 默认数据结构，可用于测试或作为数据层 DTO
/// </summary>
public struct CityEntity : ICityEntity
{
    public long Id { get; set; }
    public int TileX { get; set; }
    public int TileY { get; set; }
    public int Type { get; set; }
    public MarchRelation Relation { get; set; }
}
