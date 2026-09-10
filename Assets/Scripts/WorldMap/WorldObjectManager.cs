/*
 * 文件名: WorldObjectManager.cs
 * 作者: zengxin
 * 创建日期: 2026-7-29
 */


using System;
using System.Collections.Generic;
using UnityEngine;
/*
 世界地图对象管理器
          - 通用对象池：只服务实际渲染物（WorldCity 城池等）的进出池复用
          - 数据层：维护行军实体与"格子→行军"索引、城池数据与"格子→城池"索引
          - 表现层：WorldMapController 滚动时按格子范围调用 SetupObject/DestroyObject。
            MarchLine 是纯 C# 普通对象（业务数据生灭，不占池），只归集/调度箭头槽位；
            视口滚动通过 NotifyTileIn/NotifyTileOut 增量标记脏线（O(1)），
            帧末 FlushDirtyLines 统一重建可见段；LateUpdate 驱动各活跃线 Tick
            推进箭头位置，并交给 ArrowMeshBatcher 合并渲染（Mesh 合并，无独立
            GameObject/tween
 */


/// <summary>
/// 行军宿主接口——MarchLine 只依赖该接口，由 WorldObjectManager 实现
/// </summary>
public interface IMarchHost
{
    /// <summary>获取对应关系的箭头精灵</summary>
    Sprite GetArrowSprite(MarchRelation relation);

    /// <summary>单条行军线箭头数量上限（防止超长线箭头过多）</summary>
    int MaxArrowCount { get; }

    /// <summary>箭头 MeshRenderer 的 sortingOrder（需大于地图 tile 的 0，保证盖在地图上）</summary>
    int ArrowSortingOrder { get; }

    /// <summary>箭头合并渲染器挂载父节点（marchLayer，不旋转；方向由槽位角度体现）</summary>
    Transform ArrowParent { get; }
}

/// <summary>
/// 世界地图对象管理器
/// </summary>
public class WorldObjectManager : MonoBehaviour, IMarchHost
{
    #region 序列化字段

    [Header("层级与预制体")]
    [Tooltip("行军线挂载的 RectTransform 层级")]
    public RectTransform marchLayer;      // 行军层

    [Tooltip("城市挂载的 RectTransform 层级（尺寸由 WorldMapController 统一设置）")]
    public RectTransform cityLayer;     // 城市层

    [Tooltip("队伍挂载的 RectTransform 层级（尺寸由 WorldMapController 统一设置）")]
    public RectTransform armyLayer;     // 队伍层



    [Tooltip("对象池根节点（可为空，将自动创建）")]
    [SerializeField] private Transform _poolRoot;

    [Header("箭头配置")]
    [Tooltip("箭头渲染尺寸（content 像素；P0 起箭头走 Mesh 合并渲染，无独立 GameObject）")]
    [SerializeField] private Vector2 _arrowSize = new Vector2(22f, 24f);
    [SerializeField] private float _arrowSpacing = 5f;
    [Tooltip("箭头贴图相对 +X 轴的补偿角度。当前箭头资源朝右，默认填 0")]
    [SerializeField] private float _spriteAngleOffset = 0f;
    [Tooltip("单条行军线箭头数量上限（纯防爆防御：步距约 27px/箭头，1000 ≈ 27000px 可见轨道，远超任何视口+缓冲场景；密度由 _arrowSize/_arrowSpacing 决定，本值只挡极端长线）")]
    [SerializeField] private int _maxArrowCount = 1000;
    [Tooltip("箭头合并渲染器排序序号。需大于地图 tile 的 sortingOrder（tile 全为 0）；10000 为防御性大数")]
    [SerializeField] private int _arrowSortingOrder = 10000;

    [Header("精灵资源（位于 WorldObject 图集下）")]
    [SerializeField] private List<Sprite> _arrowSprites = new List<Sprite>();

    [Header("城池配置")]
    [Tooltip("城池预制体（WorldCity.prefab；可为空，将运行时创建最小模板）")]
    [SerializeField] private WorldCity _cityPrefab;

    [Tooltip("城池精灵列表（按 ICityEntity.Type 索引；空则用预制体自带贴图）")]
    [SerializeField] private List<Sprite> _citySprites = new List<Sprite>();

    [Tooltip("城池渲染尺寸（content 像素；与纹理分辨率、PPU 解耦）")]
    [SerializeField] private Vector2 _citySize = new Vector2(350f, 362f);

    [Tooltip("城池 SpriteRenderer 排序序号。需大于地图 tile 的 sortingOrder（tile 全为 0）")]
    [SerializeField] private int _citySortingOrder = 10000;

    [Header("对象池")]
    [Tooltip("单类型池上限，超出后直接销毁，防止无限增长。P0 起箭头不再走对象池（Mesh 合并渲染），池只服务城池等渲染物")]
    [SerializeField] private int _maxPoolSize = 10000;

    [Tooltip("初始化时城池池预热数量（城池通常远少于箭头，默认 32）。0 表示不预热")]
    [SerializeField] private int _poolPrewarmCityCount = 32;

    #endregion

    #region 运行时数据

    // 通用对象池：类型 -> 空闲队列
    private readonly Dictionary<Type, Queue<WorldObject>> _pools = new Dictionary<Type, Queue<WorldObject>>();
    // 热路径缓存：进出屏时 Spawn/Despawn 连续同类型（城池等），命中直接返回队列引用，省一次字典查找
    private Type _lastPoolType;
    private Queue<WorldObject> _lastPoolQueue;
    // 预制体：类型 -> prefab（Inspector 拖入或 RegisterPrefab 注册）
    private readonly Dictionary<Type, WorldObject> _prefabs = new Dictionary<Type, WorldObject>();

    // 行军数据层
    private readonly Dictionary<long, IMarchEntity> _marchData = new Dictionary<long, IMarchEntity>();
    // id -> 行军线覆盖的格子集合（无边缘索引）
    private readonly Dictionary<long, HashSet<int>> _marchTiles = new Dictionary<long, HashSet<int>>();
    // 格子索引（无边缘索引）-> 经过该格子的行军 id 集合
    private readonly Dictionary<int, HashSet<long>> _marchByTile = new Dictionary<int, HashSet<long>>();
    // id -> 当前存在的行军线（纯 C# 普通对象：AddMarch 创建 / RemoveMarch 丢弃，
    // 不随视口进出池——箭头渲染走 Mesh 合并，无独立对象）
    private readonly Dictionary<long, MarchLine> _marchLines = new Dictionary<long, MarchLine>();
    // 当前处于可视范围内的格子（无边缘索引）
    private readonly HashSet<int> _inRangeTiles = new HashSet<int>();

    // P0 增量驱动：本帧有格子进出（可见段需重建）的行军线集合，
    // 滚动逐格通知只 O(1) 标记，帧末 FlushDirtyLines 统一 RebuildSlots
    private readonly HashSet<MarchLine> _dirtyLines = new HashSet<MarchLine>();
    // 当前有可见箭头的活跃行军线（LateUpdate 驱动 Tick + 合并渲染；只遍历这些线）
    private readonly List<MarchLine> _activeLines = new List<MarchLine>();
    // 箭头合并渲染器（全部在屏箭头一张 Mesh，DrawCall = 纹理桶数）
    private ArrowMeshBatcher _arrowBatcher;

    // 城池数据层：id -> 城池数据（业务增删，渲染层只读）
    private readonly Dictionary<long, ICityEntity> _cityData = new Dictionary<long, ICityEntity>();
    // 格子索引（无边缘索引）-> 城池 id（一格一城，独占）
    private readonly Dictionary<int, long> _cityByTile = new Dictionary<int, long>();
    // id -> 当前在屏显示的城池对象（屏幕内复用：进屏 Spawn+Show，出屏 Despawn）
    private readonly Dictionary<long, WorldCity> _cityObjects = new Dictionary<long, WorldCity>();


    private MapMetaInfo _mapMetaInfo;

    #endregion

    #region 初始化

    /// <summary>
    /// 由 WorldMapController 在地图初始化后调用。
    /// 挂载层与缩放由上层统一处理，这里只接收地图元数据并准备池
    /// </summary>
    public void Initialize(MapMetaInfo mapMetaInfo)
    {
        _mapMetaInfo = mapMetaInfo;

        if (_poolRoot == null)
        {
            var rootGo = new GameObject("WorldObjectPoolRoot");
            rootGo.SetActive(false);
            _poolRoot = rootGo.transform;
        }

        // 只有实际渲染物走对象池：WorldCity（视口驱动进出池）。
        // MarchLine 是纯 C# 普通对象（业务生灭），不占池；箭头走 Mesh 合并渲染（无对象）。
        // 挂载层不在此配置：对象显示时由 Show 的 parent 参数决定（城池 cityLayer）
        RegisterPrefab(_cityPrefab);

        // 箭头合并渲染器：挂在行军层下（顶点为层空间/content 像素坐标，与槽位一致）
        if (marchLayer != null)
        {
            _arrowBatcher = new ArrowMeshBatcher();
            _arrowBatcher.Initialize(marchLayer, _arrowSortingOrder);
        }

        // 预热池：把首次进出屏的 Instantiate 成本集中到初始化（滚动首帧/首屏加载不卡顿）。
        // 预热对象 Recycle 清 sprite 后入池，状态与 Despawn 入池完全一致
        PrewarmPool<WorldCity>(_poolPrewarmCityCount);
    }

    /// <summary>
    /// 注册某类型对象的预制体（不注册则运行时创建最小模板）
    /// </summary>
    public void RegisterPrefab<T>(T prefab) where T : WorldObject
    {
        if (prefab != null)
            _prefabs[typeof(T)] = prefab;
    }



    #endregion

    #region 通用对象池

    /// <summary>
    /// 从池中取出一个对象（池空则创建模板）。取出的对象处于未激活状态，由调用方负责显示
    /// </summary>
    public T Spawn<T>() where T : WorldObject
    {
        Queue<WorldObject> queue = GetOrCreatePool(typeof(T));
        if (queue.Count > 0)
            return queue.Dequeue() as T;

        return CreateTemplate<T>();
    }

    /// <summary>
    /// 回收对象进池（超出池上限直接销毁）
    /// </summary>
    public void Despawn(WorldObject obj)
    {
        if (obj == null)
            return;

        obj.Recycle();
        obj.transform.SetParent(_poolRoot, false);
        obj.gameObject.SetActive(false);

        Queue<WorldObject> queue = GetOrCreatePool(obj.GetType());
        if (queue.Count < _maxPoolSize)
            queue.Enqueue(obj);
        else
            Destroy(obj.gameObject);
    }

    private Queue<WorldObject> GetOrCreatePool(Type type)
    {
        // 热路径：连续 Spawn/Despawn 几乎总是同一类型（进出屏多为城池），
        // 命中缓存直接返回，省一次字典查找（字典本身 O(1)，此处只为高频路径再省常数）
        if (_lastPoolQueue != null && type == _lastPoolType)
            return _lastPoolQueue;

        if (!_pools.TryGetValue(type, out Queue<WorldObject> queue))
        {
            queue = new Queue<WorldObject>();
            _pools[type] = queue;
        }

        _lastPoolType = type;
        _lastPoolQueue = queue;
        return queue;
    }

    private T CreateTemplate<T>() where T : WorldObject
    {
        T template;
        if (_prefabs.TryGetValue(typeof(T), out WorldObject prefab) && prefab != null)
        {
            template = Instantiate(prefab) as T;
        }
        else
        {
            // RequireComponent 会自动补齐 RectTransform / SpriteRenderer 等依赖组件
            var go = new GameObject($"WorldObject_{typeof(T).Name}", typeof(T));
            template = go.GetComponent<T>();
        }

        template.gameObject.SetActive(false);
        template.transform.SetParent(_poolRoot, false);

        return template;
    }

    /// <summary>
    /// 预热池：初始化时按估算预填对象，避免首次进出屏的 Instantiate 一次性成本。
    /// 预热对象走完整 Recycle + 入池流程，状态与 Despawn 入池完全一致（sprite 清空、挂 _poolRoot、隐藏）
    /// </summary>
    private void PrewarmPool<T>(int count) where T : WorldObject
    {
        if (count <= 0)
            return;

        Queue<WorldObject> queue = GetOrCreatePool(typeof(T));
        for (int i = 0; i < count && queue.Count < _maxPoolSize; i++)
        {
            T obj = CreateTemplate<T>();
            obj.Recycle();
            obj.transform.SetParent(_poolRoot, false);
            obj.gameObject.SetActive(false);
            queue.Enqueue(obj);
        }
    }

    #endregion

    #region 行军线数据层

    /// <summary>
    /// 新增或更新一条行军（数据层变化时调用，整对象覆盖）。
    /// MarchLine 为纯 C# 普通对象：首次创建、覆盖刷新原地更新，不占池
    /// </summary>
    public void AddMarch(IMarchEntity entity)
    {
        if (entity == null || _mapMetaInfo == null)
            return;
        // 越界校验（与 AddCity 对齐）：越界行军采样全被 AddTile 过滤 → 静默不可见、难排查，
        // 数据层应预判。起点/终点/途经点任一越界即整条拒绝
        if (IsTileOutOfRange(entity.StartTileX, entity.StartTileY) ||
            IsTileOutOfRange(entity.EndTileX, entity.EndTileY) ||
            HasWaypointOutOfRange(entity.Waypoints))
        {
            Debug.LogWarning($"[WorldMap] AddMarch(id={entity.Id}) 起点/终点/途经点瓦片越界，已忽略整条行军。");
            return;
        }

        // 覆盖式更新：先移除旧索引（不动 MarchLine，覆盖时复用同一线对象）
        RemoveMarchIndex(entity.Id);

        _marchData[entity.Id] = entity;

        // 沿线采样：格子集合（通知索引）+ 格子→轨道区间（箭头可见段计算）。
        // polyline 先构建一次，采样与 MarchLine 复用同一份（避免重复换算 + 分配）
        Vector2[] polyline = BuildPolyline(entity);
        var tileTrackRanges = new Dictionary<int, List<Vector2>>();
        HashSet<int> tiles = CalcMarchTiles(entity, polyline, tileTrackRanges);
        _marchTiles[entity.Id] = tiles;
        foreach (int tileIndex in tiles)
        {
            if (!_marchByTile.TryGetValue(tileIndex, out HashSet<long> ids))
            {
                ids = new HashSet<long>();
                _marchByTile[tileIndex] = ids;
            }
            ids.Add(entity.Id);
        }

        // 创建或原地刷新 MarchLine（池化复用：从池取或覆盖刷新同一线对象）
        if (_marchLines.TryGetValue(entity.Id, out MarchLine line))
        {
            line.Refresh(this, entity, polyline,
                _arrowSize, _arrowSpacing, _spriteAngleOffset, tileTrackRanges);
        }
        else
        {
            CreateMarch(entity.Id, polyline, tileTrackRanges);
        }

        // 创建/刷新后校准一次屏内状态（行军覆盖格子可能变化），
        // 标记脏并立即重建（业务事件即时可见；滚动路径则由帧末 FlushDirtyLines 统一重建）
        line = _marchLines[entity.Id];
        line.RefreshViewState(_marchTiles[entity.Id], _inRangeTiles);
        MarkLineDirty(line);
    }

    /// <summary>
    /// 标记行军线本帧需要重建可见段并立即执行一次统一重建（幂等）。
    /// 业务增删/覆盖刷新路径即时同步；滚动路径由 SetupObject/DestroyObject 批量标记后
    /// 帧末 FlushDirtyLines 统一重建（避免每格全量重建）
    /// </summary>
    private void MarkLineDirty(MarchLine line)
    {
        if (line == null)
            return;
        _dirtyLines.Add(line);
        FlushDirtyLines();
    }

    /// <summary>
    /// 移除行军（数据层删除/行军结束时调用）
    /// </summary>
    public void RemoveMarch(long id)
    {
        if (!_marchData.ContainsKey(id))
            return;

        RemoveMarchIndex(id);

        if (_marchLines.TryGetValue(id, out MarchLine line))
        {
            _marchLines.Remove(id);
            _dirtyLines.Remove(line);
            _activeLines.Remove(line);
            // 纯 C# 对象：清槽位与引用后直接丢弃（GC 接管，无需池；箭头走合并渲染无对象回收）
            line.Dispose();
        }
    }

    /// <summary>
    /// 仅移除行军的数据索引（_marchData/_marchTiles/_marchByTile），不动 MarchLine
    /// </summary>
    private void RemoveMarchIndex(long id)
    {
        _marchData.Remove(id);

        if (_marchTiles.TryGetValue(id, out HashSet<int> tiles))
        {
            foreach (int tileIndex in tiles)
            {
                if (_marchByTile.TryGetValue(tileIndex, out HashSet<long> ids))
                {
                    ids.Remove(id);
                    if (ids.Count == 0)
                        _marchByTile.Remove(tileIndex);
                }
            }
            _marchTiles.Remove(id);
        }
    }

    /// <summary>
    /// 刷新已有行军（路径/关系/速度变化）——数据走整对象覆盖，对象仍从池复用
    /// </summary>
    public void RefreshMarch(IMarchEntity entity)
    {
        AddMarch(entity);
    }

    #endregion

    #region 城池数据层

    /// <summary>
    /// 新增或更新一座城池（数据层变化时调用，整对象覆盖）。
    /// 城池对象走对象池屏幕内复用：所在格子当前在屏则立即显示，否则等待格子进屏
    /// </summary>
    public void AddCity(ICityEntity entity)
    {
        if (entity == null || _mapMetaInfo == null)
            return;
        // 越界校验（与 AddMarch 对齐）：越界即静默不可见、难排查，数据层应预判
        if (IsTileOutOfRange(entity.TileX, entity.TileY))
        {
            Debug.LogWarning($"[WorldMap] AddCity(id={entity.Id}) 瓦片({entity.TileX},{entity.TileY})越界，已忽略该城池。");
            return;
        }

        int tileIndex = entity.TileY * _mapMetaInfo.TileCountPerWidth + entity.TileX;

        // 覆盖式更新：同 id 旧城（可能已换格）先移除；新格若被别的城占用则先移除它（一格一城）
        RemoveCityIndex(entity.Id, tileIndex);

        _cityData[entity.Id] = entity;
        _cityByTile[tileIndex] = entity.Id;

        // 所在格子当前在屏 → 立即显示（进屏补显）
        if (_inRangeTiles.Contains(tileIndex))
            ShowCity(entity.Id);
    }

    /// <summary>
    /// 移除城池（数据层删除/城池被摧毁时调用）：清数据索引，在屏则回收对象进池
    /// </summary>
    public void RemoveCity(long id)
    {
        if (!_cityData.TryGetValue(id, out ICityEntity entity))
            return;

        int tileIndex = entity.TileY * _mapMetaInfo.TileCountPerWidth + entity.TileX;
        if (_cityByTile.TryGetValue(tileIndex, out long curId) && curId == id)
            _cityByTile.Remove(tileIndex);

        HideCity(id);
        _cityData.Remove(id);
    }

    /// <summary>
    /// 刷新已有城池（位置/类型/关系变化）——数据走整对象覆盖，显示对象从池复用
    /// </summary>
    public void RefreshCity(ICityEntity entity)
    {
        AddCity(entity);
    }

    /// <summary>
    /// 仅移除城池的数据索引（同 id 旧城 + 新格占用者），不动其他城池
    /// </summary>
    private void RemoveCityIndex(long id, int newTileIndex)
    {
        if (_cityData.TryGetValue(id, out ICityEntity old))
        {
            int oldTile = old.TileY * _mapMetaInfo.TileCountPerWidth + old.TileX;
            if (_cityByTile.TryGetValue(oldTile, out long oldId) && oldId == id)
                _cityByTile.Remove(oldTile);
            HideCity(id);
            _cityData.Remove(id);
        }

        if (_cityByTile.TryGetValue(newTileIndex, out long otherId) && otherId != id)
        {
            HideCity(otherId);
            _cityData.Remove(otherId);
            _cityByTile.Remove(newTileIndex);
        }
    }

    /// <summary>
    /// 显示城池（所在格子进屏时调用；对象从池取，已在屏则幂等）
    /// </summary>
    private void ShowCity(long id)
    {
        if (_cityObjects.ContainsKey(id))
            return;
        if (!_cityData.TryGetValue(id, out ICityEntity entity))
            return;

        _mapMetaInfo.TiledPositionToContentPoint(entity.TileX, entity.TileY, out float px, out float py);
        // 精灵：优先按类型索引取；未配置时用预制体自带贴图兜底
        // （池复用后 Recycle 会清 sprite，必须在此补齐，否则出屏再进屏城池隐形）
        Sprite sprite = GetCitySprite(entity.Type);
        if (sprite == null && _cityPrefab != null)
            sprite = _cityPrefab.Renderer.sprite;

        WorldCity city = Spawn<WorldCity>();
        city.Show(
            sprite,
            _citySortingOrder,
            cityLayer,
            new Vector3(px, py, 0f),
            _citySize);
        city.TileX = entity.TileX;
        city.TileY = entity.TileY;
        _cityObjects[id] = city;
    }

    /// <summary>
    /// 回收城池（所在格子出屏/城池移除时调用；不在屏则幂等）
    /// </summary>
    private void HideCity(long id)
    {
        if (_cityObjects.TryGetValue(id, out WorldCity city))
        {
            _cityObjects.Remove(id);
            Despawn(city);
        }
    }

    /// <summary>按城池类型取精灵（越界/未配置返回 null，WorldCity.Show 兜底用预制体贴图）</summary>
    private Sprite GetCitySprite(int type)
    {
        return type >= 0 && type < _citySprites.Count ? _citySprites[type] : null;
    }

    /// <summary>
    /// 点选城池（点击命中检测）：遍历当前在屏城池，按"点击点归一化到城包围椭圆内"判定命中，
    /// 返回归一化距离最近的城数据实体；未命中返回 null。
    /// 椭圆 = 归一化 (dx/半宽, dy/半高)：城贴图(350×362)纵略高于横，等半径圆会在城上下边缘
    /// （半径 175 vs 半高 181）漏掉 ~6px 的点击，椭圆精确贴合显示区。
    /// 且用形状判定而非所在格判定：城贴图比格子(254×127)大，玩家点中城贴图边缘
    /// 但落在邻格时也应命中，否则体验差。
    /// </summary>
    public ICityEntity PickCityAt(float contentX, float contentY)
    {
        if (_cityObjects.Count == 0)
            return null;

        long bestId = -1L;
        float bestScore = float.MaxValue;
        float halfW = _citySize.x * 0.5f;
        float halfH = _citySize.y * 0.5f;

        foreach (KeyValuePair<long, WorldCity> kv in _cityObjects)
        {
            // WorldCity 的 localPosition 即 content 像素坐标（Show 时以 content 像素放置，cityLayer 与 content 同系）
            Vector3 p = kv.Value.transform.localPosition;
            float dx = (p.x - contentX) / halfW;
            float dy = (p.y - contentY) / halfH;
            float score = dx * dx + dy * dy; // 归一化椭圆距离平方：<=1 即在城显示区内
            if (score <= 1f && score < bestScore)
            {
                bestScore = score;
                bestId = kv.Key;
            }
        }

        if (bestId < 0L)
            return null;
        _cityData.TryGetValue(bestId, out ICityEntity entity);
        return entity;
    }

    #endregion

    #region 格子生命周期（WorldMapController 按范围驱动）

    /// <summary>
    /// 格子进入可视范围：逐格通知经过该格子的 MarchLine（线内按可见段建箭头）
    /// 传入的是无边缘的瓦片坐标（与 IMarchEntity 一致，不做任何换算）
    /// </summary>
    public void SetupObject(int x, int y)
    {
        if (x < 0 || y < 0 ||
            x >= _mapMetaInfo.TileCountPerWidth || y >= _mapMetaInfo.TileCountPerHeight)
            return;

        int tileIndex = y * _mapMetaInfo.TileCountPerWidth + x;
        _inRangeTiles.Add(tileIndex);

        // 该格有城池 → 显示（屏幕内复用：从池取）
        if (_cityByTile.TryGetValue(tileIndex, out long cityId))
            ShowCity(cityId);

        // 该格有行军线 → 只更新在屏格子集合并标记脏（O(1)），
        // 可见段重建由帧末 FlushDirtyLines 统一执行——滚动逐格通知不再全量重建
        if (_marchByTile.TryGetValue(tileIndex, out HashSet<long> ids))
        {
            foreach (long id in ids)
            {
                if (_marchLines.TryGetValue(id, out MarchLine line))
                {
                    line.NotifyTileIn(tileIndex);
                    _dirtyLines.Add(line);
                }
            }
        }
    }

    /// <summary>
    /// 格子离开可视范围：逐格通知经过该格子的 MarchLine（该格轨道区间移出可见段，段外箭头回池）
    /// 传入的是无边缘的瓦片坐标（与 IMarchEntity 一致，不做任何换算）
    /// </summary>
    public void DestroyObject(int x, int y)
    {
        if (x < 0 || y < 0 ||
            x >= _mapMetaInfo.TileCountPerWidth || y >= _mapMetaInfo.TileCountPerHeight)
            return;

        int tileIndex = y * _mapMetaInfo.TileCountPerWidth + x;
        _inRangeTiles.Remove(tileIndex);

        // 该格有城池 → 回收进池（屏幕内复用）
        if (_cityByTile.TryGetValue(tileIndex, out long cityId))
            HideCity(cityId);

        // 该格有行军线 → 只更新在屏格子集合并标记脏（O(1)），帧末统一重建
        if (_marchByTile.TryGetValue(tileIndex, out HashSet<long> ids))
        {
            foreach (long id in ids)
            {
                if (_marchLines.TryGetValue(id, out MarchLine line))
                {
                    line.NotifyTileOut(tileIndex);
                    _dirtyLines.Add(line);
                }
            }
        }
    }

    #endregion

    #region IMarchHost

    public Sprite GetArrowSprite(MarchRelation relation)
    {
        int index = (int)relation; 
        return index >= 0 && index < _arrowSprites.Count ? _arrowSprites[index] : null;
    }

    public int MaxArrowCount => _maxArrowCount;

    public int ArrowSortingOrder => _arrowSortingOrder;

    public Transform ArrowParent => marchLayer;

    #endregion

    #region 增量驱动（滚动帧末统一重建 + 每帧推进渲染）

    /// <summary>
    /// 统一重建脏行军线的可见段与槽位（滚动范围更新后/每帧兜底调用，幂等）。
    /// 滚动时逐格进出只 O(1) 标记脏线，避免旧实现"每格全量重建箭头"；
    /// 重建是纯数据操作（槽位增删 + 无对象生灭），并同步活跃线集合（驱动/渲染只遍历活跃线）
    /// </summary>
    public void FlushDirtyLines()
    {
        if (_dirtyLines.Count == 0)
            return;

        foreach (MarchLine line in _dirtyLines)
        {
            line.RebuildSlots();
            if (line.HasSlots)
            {
                if (!_activeLines.Contains(line))
                    _activeLines.Add(line);
            }
            else
            {
                _activeLines.Remove(line);
            }
        }
        _dirtyLines.Clear();
    }

    /// <summary>
    /// 每帧驱动：统一重建（兜底）→ 各活跃线推进箭头位置 → 合并渲染。
    /// 只遍历活跃线（有在屏箭头），万级行军下与地图总规模解耦
    /// </summary>
    private void LateUpdate()
    {
        FlushDirtyLines();

        float dt = Time.deltaTime;
        for (int i = 0; i < _activeLines.Count; i++)
            _activeLines[i].Tick(dt);

        _arrowBatcher?.Render(_activeLines);
    }

    private void OnDestroy()
    {
        // 场景卸载/组件销毁时释放运行时资源：箭头合并渲染器的 Mesh 与材质不是场景对象，
        // 不随场景卸载自动销毁，WorldMap 若做成重复进出（Additive/关卡重载）会累积泄漏。
        // MarchLine 是纯 C# 对象（GC 接管）、池内 GameObject 随场景销毁，均无需处理。
        _arrowBatcher?.Dispose();
        _arrowBatcher = null;
        _activeLines.Clear();
        _dirtyLines.Clear();
    }

    #endregion

    #region 内部工具

    /// <summary>
    /// 计算行军线覆盖的格子集合与"格子→轨道区间"映射。
    /// 沿线按步长采样找出线穿过的全部格子（不能只按箭头落点——箭头只活在
    /// 可见段内且位置动态流动，视口滑到线的中段时该段格子必须能收到通知）。
    /// pts：已构建的折线路点（[起点, Waypoints..., 终点]，由 AddMarch 构建一次传入，
    /// 采样与 MarchLine 共用，避免重复换算）
    /// 路径 = [起点, Waypoints..., 终点] 的折线（无路点即直线），逐段采样；
    /// 轨道区间 t∈[t0,t1]：t 为沿折线的累计弧长，格子中心沿所在段投影 ± 半宽
    /// （TileWidth/2），保证格子刚进屏时其区间即完整覆盖。
    /// 一个格子可能被多段采到（拐点附近/路径绕回），故映射为区间列表
    /// </summary>
    private HashSet<int> CalcMarchTiles(IMarchEntity entity, Vector2[] pts, Dictionary<int, List<Vector2>> tileTrackRanges)
    {
        var tiles = new HashSet<int>();
        float halfWidth = _mapMetaInfo.TileWidth / 2f;
        float total = 0f;

        for (int seg = 0; seg < pts.Length - 1; seg++)
        {
            Vector2 p0 = pts[seg];
            Vector2 p1 = pts[seg + 1];
            float dx = p1.x - p0.x;
            float dy = p1.y - p0.y;
            float segLen = Mathf.Sqrt(dx * dx + dy * dy);

            // 本段经过的格子（独立收集，段间并集由全局 tiles 合并）
            var segTiles = new HashSet<int>();
            _mapMetaInfo.ContentPointToTiledPosition(p0.x, p0.y, out int p0x, out int p0y);
            AddTile(segTiles, p0x, p0y);
            // 段尾格显式登记：终点/拐点格不依赖采样步长碰运气
            //（最后采样点距段尾最多 sampleStep，极端情况下会漏掉终点格）。
            // 拐点格同时是下一段 p0，重复登记无害（HashSet 去重 + 多区间列表）
            _mapMetaInfo.ContentPointToTiledPosition(p1.x, p1.y, out int p1x, out int p1y);
            AddTile(segTiles, p1x, p1y);
            if (segLen > 0.001f)
            {
                float dirX = dx / segLen;
                float dirY = dy / segLen;

                // 沿线采样：步长取菱形格纵向半高（TileHeight/2，约 63px），
                // 小于任意相邻格中心距（≈142px），保证不跳格
                float sampleStep = _mapMetaInfo.TileHeight / 2f;
                for (float s = 0f; s < segLen; s += sampleStep)
                {
                    _mapMetaInfo.ContentPointToTiledPosition(
                        p0.x + dirX * s, p0.y + dirY * s, out int tileX, out int tileY);
                    AddTile(segTiles, tileX, tileY);
                }
            }

            // 本段格子 → 轨道区间：中心沿本段投影 + 累计弧长 ± 半宽，clamp 到 [total, total+segLen]
            foreach (int tileIndex in segTiles)
            {
                if (segLen <= 0.001f)
                {
                    AddMarchRange(tileTrackRanges, tileIndex, total, total);
                    continue;
                }

                int tx = tileIndex % _mapMetaInfo.TileCountPerWidth;
                int ty = tileIndex / _mapMetaInfo.TileCountPerWidth;
                _mapMetaInfo.TiledPositionToContentPoint(tx, ty, out float cx, out float cy);
                cy -= _mapMetaInfo.TileHeight / 2f; // 上顶点 → 格子中心
                float dirX = dx / segLen;
                float dirY = dy / segLen;
                float t = total + (cx - p0.x) * dirX + (cy - p0.y) * dirY;
                float t0 = Mathf.Max(total, t - halfWidth);
                float t1 = Mathf.Min(total + segLen, t + halfWidth);
                AddMarchRange(tileTrackRanges, tileIndex, t0, t1);
            }

            foreach (int tileIndex in segTiles)
                tiles.Add(tileIndex);
            total += segLen;
        }

        return tiles;
    }

    /// <summary>给格子的轨道区间列表追加一个区间（同一格可被多段采到）</summary>
    private void AddMarchRange(Dictionary<int, List<Vector2>> tileTrackRanges, int tileIndex, float t0, float t1)
    {
        // 零长度/负长度区间：格子中心投影落在本段边界外（线仅擦过格角），
        // 该格不参与可见段，跳过可避免可见段边界处出现静止箭头
        if (t1 - t0 <= 0.001f)
            return;

        if (!tileTrackRanges.TryGetValue(tileIndex, out List<Vector2> ranges))
        {
            ranges = new List<Vector2>(2);
            tileTrackRanges[tileIndex] = ranges;
        }
        ranges.Add(new Vector2(t0, t1));
    }

    /// <summary>
    /// 组装折线路点（层空间坐标）：[起点, Waypoints..., 终点]。
    /// 无路点 = 两点直线，与旧直线模型完全等价
    /// </summary>
    private Vector2[] BuildPolyline(IMarchEntity entity)
    {
        var list = new List<Vector2>(2);
        _mapMetaInfo.TiledPositionToContentPoint(entity.StartTileX, entity.StartTileY, out float sx, out float sy);
        list.Add(new Vector2(sx, sy));
        if (entity.Waypoints != null)
        {
            for (int i = 0; i < entity.Waypoints.Count; i++)
            {
                Vector2Int w = entity.Waypoints[i];
                _mapMetaInfo.TiledPositionToContentPoint(w.x, w.y, out float wx, out float wy);
                list.Add(new Vector2(wx, wy));
            }
        }
        _mapMetaInfo.TiledPositionToContentPoint(entity.EndTileX, entity.EndTileY, out float ex, out float ey);
        list.Add(new Vector2(ex, ey));
        return list.ToArray();
    }

    /// <summary>瓦片坐标是否越界（地图范围 [0, TileCountPerWidth) × [0, TileCountPerHeight)）</summary>
    private bool IsTileOutOfRange(int tileX, int tileY)
    {
        return tileX < 0 || tileY < 0 ||
               tileX >= _mapMetaInfo.TileCountPerWidth || tileY >= _mapMetaInfo.TileCountPerHeight;
    }

    /// <summary>途经点列表是否有任一越界（null/空 = 无，直线路径）</summary>
    private bool HasWaypointOutOfRange(IReadOnlyList<Vector2Int> waypoints)
    {
        if (waypoints == null)
            return false;
        for (int i = 0; i < waypoints.Count; i++)
        {
            if (IsTileOutOfRange(waypoints[i].x, waypoints[i].y))
                return true;
        }
        return false;
    }

    private void AddTile(HashSet<int> tiles, int tileX, int tileY)
    {
        if (tileX < 0 || tileY < 0 || tileX >= _mapMetaInfo.TileCountPerWidth || tileY >= _mapMetaInfo.TileCountPerHeight)
            return;
        tiles.Add(tileY * _mapMetaInfo.TileCountPerWidth + tileX);
    }

    /// <summary>
    /// 创建行军线（AddMarch 首次调用时；之后覆盖刷新走 line.Refresh 原地更新）。
    /// 纯 C# 普通对象直接 new，业务生灭驱动（RemoveMarch 时 Dispose 后丢弃）
    /// polyline：已组装好的折线路点（AddMarch 构建一次传入，避免重复换算）
    /// tileTrackRanges：格子→轨道区间（CalcMarchTiles 采样生成，交给线计算箭头可见段）
    /// </summary>
    private void CreateMarch(long id, Vector2[] polyline, Dictionary<int, List<Vector2>> tileTrackRanges)
    {
        if (!_marchData.TryGetValue(id, out IMarchEntity entity))
            return;

        var line = new MarchLine();

        line.Refresh(this, entity, polyline,
            _arrowSize, _arrowSpacing, _spriteAngleOffset, tileTrackRanges);

        _marchLines[id] = line;
    }

    #endregion
}
